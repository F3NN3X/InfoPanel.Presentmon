/////////////////////////////////////////////////////////////////////////////
#pragma once
/////////////////////////////////////////////////////////////////////////////
#include "PresentMonAPI.h"
#include "PMDPSharedMemory.h"
/////////////////////////////////////////////////////////////////////////////
#define APPEND_LOG(s) { OutputDebugString(s); }	
#define APPEND_LOG1(s, p1) { char czLog[256]; sprintf_s(czLog, sizeof(czLog), s, p1); OutputDebugString(czLog); }
#define APPEND_LOG2(s, p1, p2) { char czLog[256]; sprintf_s(czLog, sizeof(czLog), s, p1, p2); OutputDebugString(czLog); }
#define APPEND_LOG3(s, p1, p2, p3) { char czLog[256]; sprintf_s(czLog, sizeof(czLog), s, p1, p2, p3); OutputDebugString(czLog); }
#define APPEND_LOG4(s, p1, p2, p3, p4) { char czLog[256]; sprintf_s(czLog, sizeof(czLog), s, p1, p2, p3, p4); OutputDebugString(czLog); }
#define APPEND_LOG5(s, p1, p2, p3, p4, p5) { char czLog[256]; sprintf_s(czLog, sizeof(czLog), s, p1, p2, p3, p4, p5); OutputDebugString(czLog); }
#define APPEND_LOG6(s, p1, p2, p3, p4, p5, p6) { char czLog[256]; sprintf_s(czLog, sizeof(czLog), s, p1, p2, p3, p4, p5, p6); OutputDebugString(czLog); }
#define APPEND_LOG7(s, p1, p2, p3, p4, p5, p6, p7) { char czLog[256]; sprintf_s(czLog, sizeof(czLog), s, p1, p2, p3, p4, p5, p6, p7); OutputDebugString(czLog); }
#define APPEND_LOG8(s, p1, p2, p3, p4, p5, p6, p7, p8) { char czLog[256]; sprintf_s(czLog, sizeof(czLog), s, p1, p2, p3, p4, p5, p6, p7, p8); OutputDebugString(czLog); }
/////////////////////////////////////////////////////////////////////////////
class CPresentMonDataBuffer
{
public:
	CPresentMonDataBuffer();
	virtual ~CPresentMonDataBuffer();

	void CreateSharedMemory();
	void DestroySharedMemory();

	void OnInitialize(PM_STATUS err);
	void OnStartStream(PM_STATUS err);
	void OnGetFrameData(PM_STATUS err);
	void OnStopStream(PM_STATUS err);

	void SetStatus(DWORD dwStatus);

	void SetFrameData(uint32_t dwFrames, PMDP_FRAME_DATA* lpFrameData);
	void ResetFrameData();

protected:
	HANDLE						m_hMapFile;
	LPPMDP_SHARED_MEMORY		m_pMapAddr;
};
/////////////////////////////////////////////////////////////////////////////

