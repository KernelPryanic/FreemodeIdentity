using GTA;

namespace FreemodeIdentity {
	// The single source of truth for "what is the player REALLY", read from the live body — so no
	// caller depends on a snapshot that can desync. The spoof paints a protagonist hash onto the
	// shared model-info, so ped.Model.Hash (and Identity.Current, which reads it) lie while held; and
	// spoof.OriginalHash is only a snapshot from engage time that goes stale the moment the game swaps
	// the body under the hold. Both traps are avoided here by deciding freemode-vs-protagonist from the
	// live head blend (PedAppearance.HasFreemodeBody) — the only hash-independent tell — and reading
	// the hash directly only where it is honest.
	internal static class PlayerIdentity {
		// True when the ped's BODY is a freemode ped, regardless of any spoofed archetype hash.
		public static bool IsFreemodeBody(Ped ped) => PedAppearance.HasFreemodeBody(ped);

		// The ped's REAL model hash, seeing through a live spoof. 0 if there's no ped.
		//   - Freemode body: return the freemode model. The female/male bit comes from spoof.OriginalHash
		//     while held (reliable — the spoof only ever engages on a confirmed freemode body), else from
		//     the live hash (unspoofed there, so honest).
		//   - Otherwise: a genuine protagonist (or other) body — its raw hash is the truth.
		// Unlike trusting spoof.OriginalHash outright, the freemode-vs-protagonist decision is the LIVE
		// head blend, so a body the game swapped to a protagonist under the hold reads as that protagonist
		// (honest) instead of the stale freemode snapshot.
		public static int RealModelHash(Ped ped, Spoof spoof) {
			if (ped == null) return 0;
			if (IsFreemodeBody(ped)) {
				if (spoof != null && spoof.Held && spoof.OriginalHash != 0) {
					return unchecked((int)spoof.OriginalHash);
				}
				return ped.Model.Hash;
			}
			return ped.Model.Hash;
		}

		// The protagonist identity the player GENUINELY is (body is a real protagonist), or null when the
		// body is freemode — even if a spoof is painting a protagonist hash. Replaces the scattered
		// "!spoof.Held && Identity.Current()" checks: a freemode body is never a genuine protagonist.
		public static string GenuineProtagonist(Ped ped, Spoof spoof) {
			if (ped == null || IsFreemodeBody(ped)) return null;
			return Identity.Current();
		}
	}
}
