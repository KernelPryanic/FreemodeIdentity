#include "skill_feed.hpp"

#include "native_hook.hpp"
#include "rage.hpp"
#include "shared_state.hpp"
#include "wallet_hook.hpp"

#include <cstdint>

namespace {

// stable 0x2B7E9A4EAAA93C89 (END_TEXT_COMMAND_THEFEED_POST_STATS) -> 0x0AAAE599E05E67D2 (crossmap
// column 27, the frozen post-b2944 Legacy column that b3788 uses and Enhanced ~1013.34 shares — the
// same column the wallet STAT hooks resolve through).
constexpr uint64_t XHASH_THEFEED_POST_STATS = 0x0AAAE599E05E67D2;

rage::scrNativeHandler g_origThefeedPostStats = nullptr;

// THEFEED_POST_STATS(char* title, int icon, int step, int bar, BOOL important, char* txd, char* txn).
// Drop the post while skills are pinned so the widget never enters the feed; pass everything else
// through. Gated on skillsPinned so a genuine protagonist's real skill-ups still show. Skill titles
// are "PSF_*" (Player Stat Feed) labels; the && chain short-circuits, so a title shorter than "PSF_"
// stops at its own null terminator rather than reading past it.
void HookThefeedPostStats(rage::scrNativeCallContext* ctx) {
	if (WalletHook::State()->skillsPinned != 0) {
		const char* title = ctx->GetArg<const char*>(0);
		if (title != nullptr && title[0] == 'P' && title[1] == 'S' && title[2] == 'F' && title[3] == '_') {
			// Zero the int return (a feed id) so the caller's follow-up post-ticker has a benign value.
			if (ctx->m_ReturnValue)
				*reinterpret_cast<int*>(ctx->m_ReturnValue) = 0;
			return;
		}
	}
	g_origThefeedPostStats(ctx);
}

} // namespace

namespace SkillFeed {

bool Install() {
	return NativeHook::Install(XHASH_THEFEED_POST_STATS,
	                           reinterpret_cast<void*>(&HookThefeedPostStats),
	                           reinterpret_cast<void**>(&g_origThefeedPostStats), "THEFEED_POST_STATS");
}

} // namespace SkillFeed
