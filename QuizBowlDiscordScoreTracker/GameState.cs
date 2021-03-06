﻿using System;
using System.Collections.Generic;
using System.Linq;

namespace QuizBowlDiscordScoreTracker
{
    public partial class GameState
    {
        // Note: this needs to be under 25 if we plan on sticking with Embeds. Alternatively, we have to send out
        // multiple embeds.
        public const int ScoresListLimit = 10;

        private readonly LinkedList<PhaseState> phases;

        private ulong? readerId;
        private KeyValuePair<ulong, int>[] cachedScore;

        private readonly object phasesLock = new object();
        private readonly object readerLock = new object();

        public GameState()
        {
            this.phases = new LinkedList<PhaseState>();
            this.cachedScore = null;
            this.SetupInitialPhases();
            this.ReaderId = null;
        }

        public ulong? ReaderId
        {
            get
            {
                lock (this.readerLock)
                {
                    return this.readerId;
                }
            }
            set
            {
                lock (this.readerLock)
                {
                    this.readerId = value;
                }
            }
        }

        private PhaseState CurrentPhase => this.phases.Last.Value;

        public void ClearAll()
        {
            lock (this.phasesLock)
            {
                this.SetupInitialPhases();
            }

            this.ReaderId = null;
        }

        public void ClearCurrentRound()
        {
            lock (this.phasesLock)
            {
                this.CurrentPhase.Clear();
                this.cachedScore = null;
            }
        }

        public bool AddPlayer(ulong userId)
        {
            // readers cannot add themselves
            if (userId == this.ReaderId)
            {
                return false;
            }

            Buzz player = new Buzz()
            {
                // TODO: Consider taking this from the message. This would require passing in another parameter.
                Timestamp = DateTime.Now,
                UserId = userId
            };

            lock (this.phasesLock)
            {
                return this.CurrentPhase.AddBuzz(player);
            }
        }

        public bool WithdrawPlayer(ulong userId)
        {
            // readers cannot withdraw themselves
            if (userId == this.ReaderId)
            {
                return false;
            }

            lock (this.phasesLock)
            {
                return this.CurrentPhase.WithdrawPlayer(userId);
            }
        }

        public IEnumerable<KeyValuePair<ulong, int>> GetScores()
        {
            lock (this.phasesLock)
            {
                // This has the potential to be slow. However, a quick test on my machine shows that even with
                // 1 million phases it takes ~35 ms. That's still not great, but it's about the same as looping through
                // with a for loop. The caching should help mitigate any expensive computations
                // This gets all of the score pairs from the phases, groups them together, sums the values in the
                // grouping, and then sorts it.
                if (this.cachedScore == null)
                {
                    this.cachedScore = this.phases
                        .SelectMany(phase => phase.Scores)
                        .GroupBy(kvp => kvp.Key, kvp => kvp.Value)
                        .Select(grouping => new KeyValuePair<ulong, int>(grouping.Key, grouping.Sum()))
                        .OrderByDescending(kvp => kvp.Value)
                        .ToArray();
                }

                return this.cachedScore;
            }
        }

        public void NextQuestion()
        {
            lock (this.phasesLock)
            {
                // Add a new phase, since the last one is over
                this.phases.AddLast(new PhaseState());
            }
        }

        public void ScorePlayer(int score)
        {
            lock (this.phasesLock)
            {
                if (this.CurrentPhase.TryScore(score))
                {
                    this.cachedScore = null;
                    // Player was correct, so move on to the next phase.
                    if (score > 0)
                    {
                        this.phases.AddLast(new PhaseState());
                    }
                }
            }
        }

        public bool TryGetNextPlayer(out ulong nextPlayerId)
        {
            lock (this.phasesLock)
            {
                return this.CurrentPhase.TryGetNextPlayer(out nextPlayerId);
            }
        }

        public bool Undo(out ulong userId)
        {
            lock (this.phasesLock)
            {
                // There are three cases:
                // - The phase has actions that we can undo. Just undo the action and return true.
                // - The phase does not have actions to undo, but the previous phase does. Remove the current phase, go
                //   to the previous one, and undo that one.
                // - We haven't had any actions (start of 1st phase), so there is nothing to undo.
                bool couldUndo = this.CurrentPhase.Undo(out userId);
                while (!couldUndo && this.phases.Count > 1)
                {
                    this.phases.RemoveLast();
                    couldUndo = this.CurrentPhase.Undo(out userId);
                }

                // In the only case where nothing was undone, there's no score to calculate, so clearing the cache is harmless
                this.cachedScore = null;

                return couldUndo;
            }
        }

        private void SetupInitialPhases()
        {
            // We must always have one phase.
            this.cachedScore = null;
            this.phases.Clear();
            this.phases.AddFirst(new PhaseState());
        }
    }
}
