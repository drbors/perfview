// Interface to the ETW eventing headers generated in ETWClrProfiler.h
//
// EventRegisterETWClrProfiler(callback, callbackContext)  // Called to register
// EventUnregisterETWClrProfiler()                         // Called to unregister
// EventWrite*(...)                     // For each event, called to log an event. 
//

#include <windows.h>        // Something in here is needed for the headers in ETWClrProfiler.h TODO don't do such a blanked include. 

// This was generated by the command 
// .\MC.exe -W winmeta.xml -um -b ETWClrProfiler.man
// It defines a set of event methods (e.g EventWriteGCStart) needed to log events defined in the ETWClrProvider.man file.  
#include "ETWClrProfiler.h"

//****************************************************************************
// Ideally we would not need anything else in this header file and the 
// ETWClrProfiler.h would be sufficient.  Unfortunately, the APIs provided are 
// not sufficient because they don't allow us to do actions in response to 
// ETW events. 

// The rest of this file is a redefintion of the EventRegisterETWClrProfiler 
// API that adds this capability.

// Undefine the existing registration method
#undef EventRegisterETWClrProfiler

// This remembers the user defined callback 
EXTERN_C __declspec(selectany) PENABLECALLBACK ETWClrProfiler_pCallBack;

// We register this callback function that does the setup for the MC.EXE generated control (McGenControlCallbackV2) 
// but also calls the user defined callback. 
static inline void WINAPI ETWClrProfiler_WrapperCallback(
  LPCGUID SourceId,
  ULONG IsEnabled,
  UCHAR Level,
  ULONGLONG MatchAnyKeywords,
  ULONGLONG MatchAllKeywords,
  PEVENT_FILTER_DESCRIPTOR FilterData,
  PVOID CallbackContext
)
{
    McGenControlCallbackV2(SourceId, IsEnabled, Level, MatchAnyKeywords, MatchAllKeywords, FilterData, &ETWClrProfiler_Context);

    if (ETWClrProfiler_pCallBack != NULL)
        ETWClrProfiler_pCallBack(SourceId, IsEnabled, Level, MatchAnyKeywords, MatchAllKeywords, FilterData, CallbackContext);
}

// Replace the registration function with one that remembers a user callback 
inline HRESULT EventRegisterETWClrProfiler(PENABLECALLBACK callback, void* callBackContext)
{
    ETWClrProfiler_pCallBack = callback;
    // Note we register our ETWClrProfiler_WrapperCallback callback which does both.  
    return McGenEventRegister(&ETWClrProfiler, ETWClrProfiler_WrapperCallback, callBackContext, &ETWClrProfilerHandle);
}
