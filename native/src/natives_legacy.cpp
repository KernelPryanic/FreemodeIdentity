#include "natives_legacy.hpp"

#include "logger.hpp"
#include "pattern.hpp"

#include <windows.h>
#include <cstdint>

// Legacy (classic GTA5.exe) native-handler resolution. Legacy uses the long-standing RAGE
// scrNativeRegistration table: 256 buckets keyed by (hash & 0xFF), each a linked list of
// nodes holding up to 7 entries, with the `next` pointer, entry count, and hashes all
// obfuscated by an ADDRESS-KEYED xor (so a node must be decoded in place, at its real in-game
// address). We locate the table base via a rip-relative reference and walk it — exactly as
// FiveM's rage-scripting-five scrEngine.cpp and ivanmeler/OpenVHook do (pattern + struct, .text
// plaintext on Legacy so the in-module scan needs no decryption).
//
// CRITICAL: the table is keyed by TRANSLATED (build-specific) hashes, NOT the stable documented
// hashes — RAGE shuffles native hashes per build (confirmed empirically on b3788: a stable-hash
// lookup found nothing; the table's entries decode to flat hashes in their own bucket space).
// Callers must pass the build-translated hash; the bucket is then (translatedHash & 0xFF).
namespace {

#pragma pack(push, 1)
struct NativeRegistration {
	uint64_t nextRegBase;
	uint64_t nextRegKey;
	rage::scrNativeHandler handlers[7];
	uint32_t numEntries1;
	uint32_t numEntries2;
	uint32_t pad;
	uint64_t hashes[7 * 2]; // 7 pairs of {obfuscated hash, key}

	NativeRegistration* getNextRegistration() {
		uint32_t key = static_cast<uint32_t>(reinterpret_cast<uint64_t>(this) ^ nextRegKey);
		return reinterpret_cast<NativeRegistration*>(
		    nextRegBase ^ (static_cast<uint64_t>(key) << 32) ^ key);
	}

	uint32_t getNumEntries() {
		return static_cast<uint32_t>(reinterpret_cast<uint64_t>(&numEntries1)) ^ numEntries1 ^ numEntries2;
	}

	uint64_t getHash(uint32_t index) {
		uint64_t* pair = &hashes[2 * index];
		uint32_t key = static_cast<uint32_t>(reinterpret_cast<uint64_t>(pair) ^ pair[1]);
		return pair[0] ^ (static_cast<uint64_t>(key) << 32) ^ key;
	}
};
#pragma pack(pop)

NativeRegistration** g_registrationTable = nullptr;

} // namespace

namespace NativesLegacy {

bool Init() {
	// `cmp; jbe; mov rdx,[rbx+40]; lea rcx,[rip+disp]` — the rel32 displacement sits at +9
	// in the match. table = (loc + 4) + *(int32_t*)loc, the standard rip-relative resolve.
	uint8_t* hit = Pattern::Scan("76 32 48 8B 53 40");
	if (!hit) {
		Logger::Log("NativesLegacy::Init FAILED — registrationTable pattern not found.");
		return false;
	}
	uint8_t* loc = hit + 9;
	int32_t disp = *reinterpret_cast<int32_t*>(loc);
	g_registrationTable = reinterpret_cast<NativeRegistration**>(loc + disp + 4);
	Logger::Logf("NativesLegacy::Init OK — registrationTable @ %p", (void*)g_registrationTable);
	return true;
}

rage::scrNativeHandler GetHandler(uint64_t translatedHash) {
	if (!g_registrationTable)
		return nullptr;
	for (NativeRegistration* node = g_registrationTable[translatedHash & 0xFF]; node; node = node->getNextRegistration()) {
		uint32_t n = node->getNumEntries();
		if (n > 7)
			return nullptr; // bad decode (wrong table/build) — bail rather than read garbage
		for (uint32_t i = 0; i < n; i++) {
			if (node->getHash(i) == translatedHash)
				return node->handlers[i];
		}
	}
	return nullptr;
}

} // namespace NativesLegacy
