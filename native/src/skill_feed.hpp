#pragma once

// Suppresses the "Stamina +" skill-up feed widget the game posts while we pin skills. Our per-frame
// skill memory write (see Stats) makes a value differ from the protagonist's saved profile, which
// stats_controller.ysc reads as a skill-up and posts via THEFEED_POST_STATS. The feed API can't
// remove just that item after the fact without collateral-damaging other feed items (the wallet
// tickers, the car-radio UI), so this hooks the native and drops the post before it enters the feed.
namespace SkillFeed {

// Hook THEFEED_POST_STATS. Best-effort: a failure only means the banner shows as before, so the
// caller should NOT treat it as fatal to the rest of the shim.
bool Install();

} // namespace SkillFeed
