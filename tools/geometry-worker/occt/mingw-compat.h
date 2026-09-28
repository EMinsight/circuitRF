/* Force-included into every OCCT C++ file on Windows (build.cmd: -include). Two gaps in OCCT 8.0.1 that
 * only a MinGW toolchain -- llvm-mingw, which build.cmd uses -- runs into. Neither is a patch to OCCT's
 * source; each is a declaration OCCT assumes and this toolchain does not supply.
 *
 * 1. NCollection_IncAllocator.cxx uses std::lock_guard having included only <shared_mutex>. MSVC's
 *    library pulls <mutex> in from there; libc++ does not.
 *
 * 2. Standard::AllocateAligned has no ARM64 MinGW branch (its MinGW branch is x86 only), so it falls
 *    through to posix_memalign, which Windows lacks, and Standard::FreeAligned then releases with plain
 *    free(). Whatever this returns must therefore BE malloc memory, which is 16-byte aligned on 64-bit
 *    Windows (MEMORY_ALLOCATION_ALIGNMENT). Every caller in the toolkits the recipe builds asks for 16
 *    (NCollection_AliasedArray's default); a larger alignment is refused with ENOMEM, which OCCT raises
 *    as Standard_OutOfMemory, rather than returned misaligned. */
#ifndef CRF_OCCT_MINGW_COMPAT_H
#define CRF_OCCT_MINGW_COMPAT_H

#ifdef __cplusplus
  #include <mutex>
#endif

#if defined(__MINGW32__) && defined(__aarch64__)
  #include <errno.h>
  #include <stdlib.h>
  #ifdef __cplusplus
extern "C" {
  #endif
inline int posix_memalign(void** thePtr, size_t theAlign, size_t theSize)
{
  if (theAlign > 16)
    return ENOMEM;
  *thePtr = malloc(theSize);
  return *thePtr != NULL ? 0 : ENOMEM;
}
  #ifdef __cplusplus
}
  #endif
#endif

#endif
