// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Logging;
using osu.Framework.Screens;
using osu.Game.Graphics.UserInterface;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Rooms;
using osu.Game.Scoring;
using osu.Game.Screens.Play;
using osu.Game.Screens.Play.HUD;
using osu.Game.Screens.Ranking;
using osu.Game.Users;
using osuTK;

namespace osu.Game.Screens.OnlinePlay.Multiplayer
{
    public class MultiplayerPlayer : RoomSubmittingPlayer
    {
        protected override bool PauseOnFocusLost => false;

        // Disallow fails in multiplayer for now.
        protected override bool CheckModsAllowFailure() => false;

        protected override UserActivity InitialActivity => new UserActivity.InMultiplayerGame(Beatmap.Value.BeatmapInfo, Ruleset.Value);

        [Resolved]
        private MultiplayerClient client { get; set; }

        private IBindable<bool> isConnected;

        private readonly TaskCompletionSource<bool> resultsReady = new TaskCompletionSource<bool>();

        private MultiplayerGameplayLeaderboard leaderboard;

        private readonly MultiplayerRoomUser[] users;

        private readonly Bindable<bool> leaderboardExpanded = new BindableBool();

        private LoadingLayer loadingDisplay;
        private FillFlowContainer leaderboardFlow;

        /// <summary>
        /// Construct a multiplayer player.
        /// </summary>
        /// <param name="room">The room.</param>
        /// <param name="playlistItem">The playlist item to be played.</param>
        /// <param name="users">The users which are participating in this game.</param>
        public MultiplayerPlayer(Room room, PlaylistItem playlistItem, MultiplayerRoomUser[] users)
            : base(room, playlistItem, new PlayerConfiguration
            {
                AllowPause = false,
                AllowRestart = false,
                AllowSkipping = false,
            })
        {
            this.users = users;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            if (!LoadedBeatmapSuccessfully)
                return;

            HUDOverlay.Add(leaderboardFlow = new FillFlowContainer
            {
                AutoSizeAxes = Axes.Both,
                Direction = FillDirection.Vertical,
                Spacing = new Vector2(5)
            });

            HUDOverlay.HoldingForHUD.BindValueChanged(_ => updateLeaderboardExpandedState());
            LocalUserPlaying.BindValueChanged(_ => updateLeaderboardExpandedState(), true);

            // todo: this should be implemented via a custom HUD implementation, and correctly masked to the main content area.
            LoadComponentAsync(leaderboard = new MultiplayerGameplayLeaderboard(GameplayState.Ruleset.RulesetInfo, ScoreProcessor, users), l =>
            {
                if (!LoadedBeatmapSuccessfully)
                    return;

                leaderboard.Expanded.BindTo(leaderboardExpanded);

                leaderboardFlow.Insert(0, l);

                if (leaderboard.TeamScores.Count >= 2)
                {
                    LoadComponentAsync(new GameplayMatchScoreDisplay
                    {
                        Team1Score = { BindTarget = leaderboard.TeamScores.First().Value },
                        Team2Score = { BindTarget = leaderboard.TeamScores.Last().Value },
                        Expanded = { BindTarget = HUDOverlay.ShowHud },
                    }, scoreDisplay => leaderboardFlow.Insert(1, scoreDisplay));
                }
            });

            LoadComponentAsync(new GameplayChatDisplay(Room)
            {
                Expanded = { BindTarget = leaderboardExpanded },
            }, chat => leaderboardFlow.Insert(2, chat));

            HUDOverlay.Add(loadingDisplay = new LoadingLayer(true) { Depth = float.MaxValue });
        }

        protected override void LoadAsyncComplete()
        {
            base.LoadAsyncComplete();

            if (!LoadedBeatmapSuccessfully)
                return;

            if (!ValidForResume)
                return; // token retrieval may have failed.

            client.GameplayStarted += onGameplayStarted;
            client.ResultsReady += onResultsReady;

            ScoreProcessor.HasCompleted.BindValueChanged(completed =>
            {
                // wait for server to tell us that results are ready (see SubmitScore implementation)
                loadingDisplay.Show();
            });

            isConnected = client.IsConnected.GetBoundCopy();
            isConnected.BindValueChanged(connected => Schedule(() =>
            {
                if (!connected.NewValue)
                {
                    // messaging to the user about this disconnect will be provided by the MultiplayerMatchSubScreen.
                    failAndBail();
                }
            }), true);
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            Debug.Assert(client.Room != null);
        }

        protected override void StartGameplay()
        {
            if (client.LocalUser?.State == MultiplayerUserState.Loaded)
            {
                // block base call, but let the server know we are ready to start.
                loadingDisplay.Show();
                client.ChangeState(MultiplayerUserState.ReadyForGameplay);
            }
        }

        private void updateLeaderboardExpandedState() =>
            leaderboardExpanded.Value = !LocalUserPlaying.Value || HUDOverlay.HoldingForHUD.Value;

        private void failAndBail(string message = null)
        {
            if (!string.IsNullOrEmpty(message))
                Logger.Log(message, LoggingTarget.Runtime, LogLevel.Important);

            Schedule(() => PerformExit(false));
        }

        protected override void Update()
        {
            base.Update();

            if (!LoadedBeatmapSuccessfully)
                return;

            adjustLeaderboardPosition();
        }

        private void adjustLeaderboardPosition()
        {
            const float padding = 44; // enough margin to avoid the hit error display.

            leaderboardFlow.Position = new Vector2(padding, padding + HUDOverlay.TopScoringElementsHeight);
        }

        private void onGameplayStarted() => Scheduler.Add(() =>
        {
            if (!this.IsCurrentScreen())
                return;

            loadingDisplay.Hide();
            base.StartGameplay();
        });

        private void onResultsReady()
        {
            // Schedule is required to ensure that `TaskCompletionSource.SetResult` is not called more than once.
            // A scenario where this can occur is if this instance is not immediately disposed (ie. async disposal queue).
            Schedule(() =>
            {
                if (!this.IsCurrentScreen())
                    return;

                resultsReady.SetResult(true);
            });
        }

        protected override async Task PrepareScoreForResultsAsync(Score score)
        {
            await base.PrepareScoreForResultsAsync(score).ConfigureAwait(false);

            await client.ChangeState(MultiplayerUserState.FinishedPlay).ConfigureAwait(false);

            // Await up to 60 seconds for results to become available (6 api request timeouts).
            // This is arbitrary just to not leave the player in an essentially deadlocked state if any connection issues occur.
            await Task.WhenAny(resultsReady.Task, Task.Delay(TimeSpan.FromSeconds(60))).ConfigureAwait(false);
        }

        protected override ResultsScreen CreateResults(ScoreInfo score)
        {
            Debug.Assert(Room.RoomID.Value != null);

            return leaderboard.TeamScores.Count == 2
                ? new MultiplayerTeamResultsScreen(score, Room.RoomID.Value.Value, PlaylistItem, leaderboard.TeamScores)
                : new MultiplayerResultsScreen(score, Room.RoomID.Value.Value, PlaylistItem);
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            if (client != null)
            {
                client.GameplayStarted -= onGameplayStarted;
                client.ResultsReady -= onResultsReady;
            }
        }
    }
}
