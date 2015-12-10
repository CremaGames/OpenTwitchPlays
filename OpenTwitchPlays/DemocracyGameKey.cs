using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace OpenTwitchPlays {
    public class DemocracyGameKey {
        public string username;
        public GameKey gameKey;
        public int delay;
        public int votes;

        public DemocracyGameKey(string username, GameKey gameKey, int delay) {
            this.username = username;
            this.gameKey = gameKey;
            this.delay = delay;
            votes = 1;
        }
    }

    public class DemocracyGameKeyComparer : Comparer<DemocracyGameKey> {
        public override int Compare(DemocracyGameKey a, DemocracyGameKey b) {
            return b.votes.CompareTo(a.votes);
        }
    }
}
