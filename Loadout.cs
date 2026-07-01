using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using GTA;
using GTA.Native;

namespace FreemodeIdentity {
	// Preserves a freemode character's weapons, armor and health across the things the game won't
	// keep for them — a freemode ped is not a real save subject, so an in-game save restores NONE of
	// it (verified), and our own appearance re-apply does a SET_PLAYER_MODEL that spawns a fresh,
	// bare ped. C# owns the state and persists it to a file in the mod's %APPDATA% dir, exactly like
	// the wallet (the one place Enhanced doesn't lock). A periodic sampler keeps the snapshot current;
	// the orchestrator replays it onto a freshly-recreated ped.
	//
	// Spending/cash is NOT here — that's the wallet. This is purely the carryables an identity loses.
	internal sealed class Loadout {
		public const string DefaultStoreFileName = "loadout.dat";
		// File-format version, written as the first line. Bumped when the line shape changes so a future
		// format can detect-and-skip an old file cleanly rather than mis-parse it. v2 added per-weapon
		// component lists; the loader still accepts a v1 file (weapons without components).
		const int FormatVersion = 2;

		// One attached component: the part hash (scope/suppressor/grip/clip/livery) plus its own tint
		// index — the camo COLOUR, which is keyed per-component, not per-weapon (0 for parts that don't
		// tint). Name is the resolved display name, written only as a readability comment — the hash is
		// the source of truth and the loader never reads the name back.
		struct ComponentState {
			public uint Hash;
			public int Tint;
			public string Name;
		}

		struct WeaponState {
			public uint Hash;
			public int Ammo;
			public int Tint;
			public string Name;
			public List<ComponentState> Components;
		}

		readonly List<WeaponState> weapons = new List<WeaponState>();
		uint equippedHash;
		int armor;
		int health;
		// The exact text last written to disk, so a sample that captured nothing new skips the write —
		// at a 1-2s period an unchanged re-write would be pure disk churn. Null until first persisted.
		string lastSaved;

		// Per-instance store file so a second Loadout (the protagonist's original) persists to its own
		// file instead of clobbering the primary loadout.dat.
		readonly string storeFileName;

		// Defaults to the primary loadout.dat; pass a distinct name (e.g. loadout.orig.dat) for a
		// separate store like the captured protagonist original.
		public Loadout(string storeFileName = DefaultStoreFileName) {
			this.storeFileName = storeFileName;
		}

		// Which protagonist char index (0/1/2) this snapshot was captured from, so the orig store can
		// refuse to replay onto a DIFFERENT character if the base was swapped mid-spoof (weapons aren't
		// char-namespaced, so nothing else would catch it). -1 = unbound: the primary char-agnostic
		// loadout.dat never binds, so its restore passes through. Mirrors Skills' capturedChar guard.
		int capturedChar = -1;

		// True when this store's snapshot is safe to replay for the given return char: either it's
		// unbound (primary store, char-agnostic) or it was captured from that same character.
		public bool MatchesChar(int charIdx) => capturedChar < 0 || capturedChar == charIdx;

		// Drop everything held, so the orig store can't restore a PRIOR session's snapshot if this
		// session's capture faults before it lands (a persisted .orig.dat outlives a restart). The
		// capture site clears first, then captures; a fault then restores nothing rather than stale gear.
		public void Clear() {
			weapons.Clear();
			equippedHash = 0;
			armor = 0;
			health = 0;
			capturedChar = -1;
		}

		public bool HasWeapons => weapons.Count > 0;
		public int WeaponCount => weapons.Count;
		public int Armor => armor;
		public int Health => health;

		// True when the live ped carries nothing beyond what the store holds — no weapon the store lacks,
		// and no weapon with MORE ammo than the store has. After a restore this is the "settled" signal:
		// while false, a re-grant is still pending (re-assert instead of sampling); once true, sampling is
		// safe. The ammo check matters for confiscation: a bust zeroes the store's ammo, but the recreate
		// re-grants full ammo, and without catching that the sampler would recapture it and undo the take.
		// Unarmed is ignored (never a stored inventory item). No-fault: a bad read reports settled so a
		// transient can't wedge the caller in a re-assert loop.
		public bool MatchesLive(Ped ped) {
			if (ped == null || !ped.Exists()) {
				return true;
			}
			try {
				foreach (WeaponHash wh in ped.Weapons.GetAllWeaponHashes()) {
					if (wh == WeaponHash.Unarmed) {
						continue;
					}
					int idx = weapons.FindIndex(w => w.Hash == (uint)wh);
					if (idx < 0) {
						return false; // a weapon on the ped that the store doesn't have — a re-grant
					}
					int liveAmmo = Function.Call<int>(Hash.GET_AMMO_IN_PED_WEAPON, ped, (uint)wh);
					if (liveAmmo > weapons[idx].Ammo) {
						return false; // more ammo than stored — a re-grant (e.g. after a bust zeroed it)
					}
				}
			} catch {
				return true;
			}
			return true;
		}

		string StorePath => ScriptPaths.For(storeFileName);

		// --- Sampling -----------------------------------------------------------------------

		// Snapshot the live ped's state and persist. Each group is gated by its toggle so a disabled
		// feature is neither read nor overwritten in the store (its last-saved value survives a session
		// where the user had it off). Wrapped whole: weapon enumeration touches CPedInventory memory,
		// so a bad read yields no update rather than faulting the tick. Returns true only when the
		// snapshot actually changed and was written — so the caller can log a real change, not every tick.
		// charIdx binds the snapshot to a protagonist (orig store) so a later restore can refuse a
		// mismatched character; leave it -1 (the default) for the primary char-agnostic store.
		public bool CaptureFrom(Ped ped, bool weaponsOn, bool armorOn, bool healthOn, int charIdx = -1) {
			if (ped == null || !ped.Exists()) {
				return false;
			}
			try {
				if (weaponsOn) {
					// Commit only a clean read: a transitional ped mid model-swap reports garbage inventory
					// before its real weapons stream in, and mirroring that would overwrite a good store with
					// junk (the "Invalid weapon in the protagonist store" bug). A null read leaves it untouched.
					uint scratchEquipped = (uint)ped.Weapons.Current.Hash;
					var scratch = CaptureWeapons(ped);
					if (scratch == null) {
						return false;
					}
					weapons.Clear();
					weapons.AddRange(scratch);
					equippedHash = scratchEquipped;
				}
				if (armorOn) {
					armor = ped.Armor;
				}
				if (healthOn) {
					health = ped.Health;
				}
				capturedChar = charIdx;
			} catch (Exception e) {
				Logger.LogError("Loadout.CaptureFrom: " + e);
				return false;
			}
			return Save();
		}

		// Read the ped's weapons into a fresh list. Returns null if any enumerated hash isn't a real weapon
		// (a transitional ped mid model-swap briefly reports garbage inventory entries) — the caller keeps
		// its last good snapshot rather than commit a corrupt one. A genuinely empty inventory returns an
		// empty list (a valid "carrying nothing" read), distinct from a faulted one.
		static List<WeaponState> CaptureWeapons(Ped ped) {
			var captured = new List<WeaponState>();
			foreach (WeaponHash wh in ped.Weapons.GetAllWeaponHashes()) {
				uint hash = (uint)wh;
				// Unarmed isn't a real inventory item to re-give; the equipped-weapon restore selects it
				// if that's what the player was holding.
				if (hash == (uint)WeaponHash.Unarmed) {
					continue;
				}
				if (!IsRealWeapon(wh)) {
					return null; // a garbage hash off a transitional ped — this whole read is suspect
				}
				Weapon weapon = ped.Weapons[wh];
				captured.Add(new WeaponState {
					Hash = hash,
					Ammo = Function.Call<int>(Hash.GET_AMMO_IN_PED_WEAPON, ped, hash),
					Tint = Function.Call<int>(Hash.GET_PED_WEAPON_TINT_INDEX, ped, hash),
					Name = ResolveName(Weapon.GetDisplayNameFromHash(wh)),
					Components = CaptureComponents(ped, weapon),
				});
			}
			return captured;
		}

		// Whether an enumerated hash is a genuine weapon. A transitional ped (mid model-swap) reports garbage
		// inventory hashes whose display name is the game's "Invalid" sentinel — checked as both the raw
		// label and its localized text, since a garbage hash resolves to the sentinel either way.
		static bool IsRealWeapon(WeaponHash wh) {
			string label = Weapon.GetDisplayNameFromHash(wh);
			if (string.IsNullOrEmpty(label) || label == "WTT_INVALID" || label == "Invalid") {
				return false;
			}
			return Game.GetLocalizedString(label) != "Invalid";
		}

		// The attachments actually fitted to this weapon. The candidate list comes from SHVDN's
		// compatible-components reader (a build-specific memory read, wrapped no-fault — a bad read on an
		// unverified build just yields no components, never a crash); each candidate is then confirmed
		// with the stable HAS_PED_GOT_WEAPON_COMPONENT native. Livery/camo parts are ordinary components,
		// so this also captures the gun's "skin"; each part's own tint index is the camo colour.
		static List<ComponentState> CaptureComponents(Ped ped, Weapon weapon) {
			var list = new List<ComponentState>();
			if (weapon == null) {
				return list;
			}
			foreach (WeaponComponent c in weapon.Components) {
				if (!c.Active) {
					continue;
				}
				list.Add(new ComponentState {
					Hash = (uint)c.ComponentHash,
					Tint = Function.Call<int>(Hash.GET_PED_WEAPON_COMPONENT_TINT_INDEX, ped, weapon.Hash, (uint)c.ComponentHash),
					Name = ResolveName(c.DisplayName),
				});
			}
			return list;
		}

		// Resolve a weapon/component display-name LABEL (e.g. "WT_PIST") to its localized text ("Pistol")
		// for the readability comment only. Falls back to the raw label, then "?", when the text system
		// has no entry — the comment is decoration, so a miss is cosmetic and never affects restore.
		static string ResolveName(string label) {
			if (string.IsNullOrEmpty(label)) {
				return "?";
			}
			string text = Game.GetLocalizedString(label);
			return string.IsNullOrEmpty(text) ? label : text;
		}

		// --- Restore ------------------------------------------------------------------------

		// Make the ped's weapons match the store exactly: strip everything, then give back what's stored.
		// Stripping first is what lets an intentional empty stick — the game re-grants a recreated MP ped
		// its old inventory, so an additive restore of an empty store would leave that arsenal in place.
		// equipNow is false on the gives so they don't fight each other — the final select decides what's
		// in hand.
		public void RestoreWeapons(Ped ped) {
			if (ped == null || !ped.Exists()) {
				return;
			}
			try {
				Function.Call(Hash.REMOVE_ALL_PED_WEAPONS, ped, false);
				foreach (WeaponState w in weapons) {
					Function.Call(Hash.GIVE_WEAPON_TO_PED, ped, w.Hash, w.Ammo, false, false);
					// Fit the saved attachments before tint, so a livery/camo part is present when its
					// colour is applied.
					if (w.Components != null) {
						foreach (ComponentState c in w.Components) {
							Function.Call(Hash.GIVE_WEAPON_COMPONENT_TO_PED, ped, w.Hash, c.Hash);
							if (c.Tint != 0) {
								Function.Call(Hash.SET_PED_WEAPON_COMPONENT_TINT_INDEX, ped, w.Hash, c.Hash, c.Tint);
							}
						}
					}
					if (w.Tint != 0) {
						Function.Call(Hash.SET_PED_WEAPON_TINT_INDEX, ped, w.Hash, w.Tint);
					}
				}
				if (equippedHash != 0 && equippedHash != (uint)WeaponHash.Unarmed) {
					Function.Call(Hash.SET_CURRENT_PED_WEAPON, ped, equippedHash, true);
				}
			} catch (Exception e) {
				Logger.LogError("Loadout.RestoreWeapons: " + e);
			}
		}

		// Replay armor/health. Each is applied only when a real value was stored, so an unsampled
		// group (its toggle was off, store empty) leaves the ped's current value alone. Restored only
		// on appearance-enable / cold load by the caller — never after a death-respawn, where re-filling
		// what the game just reset would soften death.
		public void RestoreVitals(Ped ped, bool armorOn, bool healthOn) {
			if (ped == null || !ped.Exists()) {
				return;
			}
			try {
				if (armorOn && armor > 0) {
					Function.Call(Hash.SET_PED_ARMOUR, ped, armor);
				}
				if (healthOn && health > 0) {
					ped.Health = health;
				}
			} catch (Exception e) {
				Logger.LogError("Loadout.RestoreVitals: " + e);
			}
		}

		// --- Confiscation ------------------------------------------------------------------

		// GTA SP's arrest confiscation: the busted player permanently loses the drawn weapon, all ammo and
		// body armor, plus the Carbine Rifle and Nightstick if held; the rest of the inventory stays but
		// empty. Edits the STORE so the taken gear never comes back through RestoreLoadout, AND strips the
		// LIVE ped now so the loss shows immediately rather than only after the later clobber re-apply.
		// The store edit is what makes it stick — a live strip alone would be undone by the next restore.
		// Persists so it survives a restart. Returns true when something was actually taken (caller logs).
		public bool ConfiscateForArrest(Ped ped) {
			int beforeCount = weapons.Count;
			int beforeArmor = armor;
			// The drawn weapon is whatever was equipped at capture; the two always-confiscated specials
			// are taken even when not in hand (matches SP).
			bool Taken(uint h) => h == equippedHash
				|| h == (uint)WeaponHash.CarbineRifle
				|| h == (uint)WeaponHash.Nightstick;
			weapons.RemoveAll(w => Taken(w.Hash));
			// All ammo gone: zero every surviving weapon's stored ammo so the restore re-gives empty guns.
			for (int i = 0; i < weapons.Count; i++) {
				WeaponState w = weapons[i];
				w.Ammo = 0;
				weapons[i] = w;
			}
			// Mirror it onto the live ped for an immediate effect: remove the taken weapons, zero the rest's
			// ammo, drop armor. Best-effort and no-fault — the store edit above is the authority.
			if (ped != null && ped.Exists()) {
				try {
					foreach (WeaponHash wh in ped.Weapons.GetAllWeaponHashes()) {
						if (Taken((uint)wh)) {
							Function.Call(Hash.REMOVE_WEAPON_FROM_PED, ped, (uint)wh);
						} else if (wh != WeaponHash.Unarmed) {
							Function.Call(Hash.SET_PED_AMMO, ped, (uint)wh, 0);
						}
					}
					Function.Call(Hash.SET_PED_ARMOUR, ped, 0);
				} catch (Exception e) {
					Logger.LogError("Loadout.ConfiscateForArrest (live strip): " + e);
				}
			}
			equippedHash = 0;
			armor = 0;
			bool took = weapons.Count != beforeCount || beforeArmor > 0;
			Save();
			return took;
		}

		// --- Persistence --------------------------------------------------------------------
		// One line-oriented text file: a "version" line, a header line per scalar, then one line per
		// weapon — "weapon <hash> <ammo> <tint> [compHash:compTint ...]  # Name [Comp, Comp]". Everything
		// from '#' on is a readability comment the loader strips; the hashes before it are the truth.
		// Line-oriented (not XML) on purpose: this is a hot, machine-only runtime mirror rewritten every
		// sample, sibling to the plain-text wallet.dat — distinct from the user-facing XML appearance
		// slots. A missing file is the valid empty state; a malformed line is skipped, not fatal. Never
		// throws — the in-memory state still works for the session even if a write fails.

		public void Load() {
			try {
				if (!File.Exists(StorePath)) {
					return;
				}
				weapons.Clear();
				foreach (string raw in File.ReadAllLines(StorePath)) {
					// Drop any trailing "# ..." readability comment before parsing — the data is the hash
					// tokens before it; the name annotation is never read back.
					int comment = raw.IndexOf('#');
					string line = (comment >= 0 ? raw.Substring(0, comment) : raw).Trim();
					if (line.Length == 0) {
						continue;
					}
					string[] f = line.Split(' ');
					switch (f[0]) {
						case "version":
							// Accepted and ignored: every shipped version's lines are forward-readable here
							// (v1 weapon lines simply carry no component tokens), so the tag is for future
							// format jumps that need to detect-and-skip rather than for branching today.
							break;
						case "char":
							// The captured protagonist index; only the orig store writes it. Out-of-range or
							// missing leaves capturedChar at -1 (unbound), which the restore treats as no guard.
							if (!(f.Length > 1 && int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out capturedChar)
									&& capturedChar >= 0 && capturedChar <= 2)) {
								capturedChar = -1;
							}
							break;
						case "armor":
							int.TryParse(f.Length > 1 ? f[1] : "0", NumberStyles.Integer, CultureInfo.InvariantCulture, out armor);
							break;
						case "health":
							int.TryParse(f.Length > 1 ? f[1] : "0", NumberStyles.Integer, CultureInfo.InvariantCulture, out health);
							break;
						case "equipped":
							uint.TryParse(f.Length > 1 ? f[1] : "0", NumberStyles.Integer, CultureInfo.InvariantCulture, out equippedHash);
							break;
						case "weapon":
							if (f.Length >= 4
									&& uint.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint wh)
									&& int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ammo)
									&& int.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int tint)) {
								weapons.Add(new WeaponState { Hash = wh, Ammo = ammo, Tint = tint, Components = ParseComponents(f, 4) });
							}
							break;
					}
				}
			} catch (Exception ex) {
				Logger.LogError($"Loadout: load failed ({ex.GetType().Name}) — starting empty.");
				weapons.Clear();
			}
			// Seed the change-detection baseline so an unchanged first sample after load doesn't rewrite
			// an identical file. Re-serialized (not the raw file bytes) so it matches what Save produces.
			lastSaved = Serialize();
			Logger.Log($"Loadout: loaded weapons={weapons.Count} armor={armor} health={health}.");
		}

		// Parse the trailing "compHash:compTint" tokens of a weapon line (from index `start`). A v1 file
		// has none, yielding an empty list. A malformed token is skipped.
		static List<ComponentState> ParseComponents(string[] fields, int start) {
			var list = new List<ComponentState>();
			for (int i = start; i < fields.Length; i++) {
				string[] kv = fields[i].Split(':');
				if (kv.Length == 2
						&& uint.TryParse(kv[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint ch)
						&& int.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ct)) {
					list.Add(new ComponentState { Hash = ch, Tint = ct });
				}
			}
			return list;
		}

		// Persist the snapshot, returning true only when it actually changed and was written. Skips the
		// write when nothing changed since the last persist — at a tight sample period most samples are
		// identical, and an unchanged re-write is pure disk churn.
		bool Save() {
			string text = Serialize();
			if (text == lastSaved) {
				return false;
			}
			try {
				File.WriteAllText(StorePath, text);
				lastSaved = text;
				return true;
			} catch {
				// swallow — a failed write must never crash the mod; the in-memory state still works
				return false;
			}
		}

		string Serialize() {
			var sb = new StringBuilder();
			sb.Append("version ").Append(FormatVersion.ToString(CultureInfo.InvariantCulture)).Append('\n');
			// The orig store records which protagonist it captured so a restore can refuse a mismatched
			// character; absent on the primary char-agnostic store (capturedChar stays -1).
			if (capturedChar >= 0) {
				sb.Append("char ").Append(capturedChar.ToString(CultureInfo.InvariantCulture)).Append('\n');
			}
			sb.Append("armor ").Append(armor.ToString(CultureInfo.InvariantCulture)).Append('\n');
			sb.Append("health ").Append(health.ToString(CultureInfo.InvariantCulture)).Append('\n');
			sb.Append("equipped ").Append(equippedHash.ToString(CultureInfo.InvariantCulture)).Append('\n');
			foreach (WeaponState w in weapons) {
				sb.Append("weapon ")
					.Append(w.Hash.ToString(CultureInfo.InvariantCulture)).Append(' ')
					.Append(w.Ammo.ToString(CultureInfo.InvariantCulture)).Append(' ')
					.Append(w.Tint.ToString(CultureInfo.InvariantCulture));
				if (w.Components != null) {
					foreach (ComponentState c in w.Components) {
						sb.Append(' ')
							.Append(c.Hash.ToString(CultureInfo.InvariantCulture)).Append(':')
							.Append(c.Tint.ToString(CultureInfo.InvariantCulture));
					}
				}
				AppendNameComment(sb, w);
				sb.Append('\n');
			}
			return sb.ToString();
		}

		// Append the human-readable "# Weapon [Comp, Comp]" trailer to a weapon line. Decoration only,
		// stripped by Load — there purely so someone opening loadout.dat can read what each hash is.
		static void AppendNameComment(StringBuilder sb, WeaponState w) {
			if (string.IsNullOrEmpty(w.Name)) {
				return;
			}
			sb.Append("  # ").Append(w.Name);
			if (w.Components != null && w.Components.Count > 0) {
				sb.Append(" [");
				for (int i = 0; i < w.Components.Count; i++) {
					if (i > 0) {
						sb.Append(", ");
					}
					sb.Append(w.Components[i].Name ?? "?");
				}
				sb.Append(']');
			}
		}
	}
}
