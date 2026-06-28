#include "build_edition.hpp"

#include <windows.h>

namespace BuildEdition {

namespace {
Edition Detect() {
	// Enhanced ships as GTA5_Enhanced.exe; Legacy as the long-standing GTA5.exe. The Enhanced
	// module is the reliable positive tell — its absence means Legacy.
	return GetModuleHandleA("GTA5_Enhanced.exe") != nullptr ? Edition::Enhanced : Edition::Legacy;
}
} // namespace

Edition Current() {
	static Edition e = Detect();
	return e;
}

bool IsEnhanced() { return Current() == Edition::Enhanced; }
bool IsLegacy() { return Current() == Edition::Legacy; }

const char* Name() { return IsEnhanced() ? "Enhanced" : "Legacy"; }

} // namespace BuildEdition
