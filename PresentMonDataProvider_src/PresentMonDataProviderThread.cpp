// PresentMonDataProviderThread.cpp : implementation file
//
// created by Unwinder
/////////////////////////////////////////////////////////////////////////////
#include "pch.h"
#include "PresentMonDataProvider.h"
#include "PresentMonDataProviderThread.h"
#include "PresentMonConsoleWrapper.h"
#include "RTSSSharedMemory.h"
#include "PresentMonAPI.h"

#include <io.h>
/////////////////////////////////////////////////////////////////////////////
#define PM_FRAME_DATA_MAX 1024
/////////////////////////////////////////////////////////////////////////////
#ifdef _DEBUG
#define new DEBUG_NEW
#undef THIS_FILE
static char THIS_FILE[] = __FILE__;
#endif
/////////////////////////////////////////////////////////////////////////////
// CPresentMonDataProviderThread
/////////////////////////////////////////////////////////////////////////////
IMPLEMENT_DYNCREATE(CPresentMonDataProviderThread, CWinThread)
/////////////////////////////////////////////////////////////////////////////
CPresentMonDataProviderThread::CPresentMonDataProviderThread()
{
}
/////////////////////////////////////////////////////////////////////////////
CPresentMonDataProviderThread::CPresentMonDataProviderThread(CPresentMonDataBuffer* lpDataBuffer)
{
	m_lpDataBuffer		= lpDataBuffer;
	m_hEventKill		= CreateEvent(NULL, TRUE, FALSE, NULL);
	m_bAutoDelete		= FALSE;

	m_dwActiveProcessId				= 0;
	m_dwTargetProcessId				= 0;
	m_dwForegroundProcessId			= 0;
	m_dwForegroundProcessTimestamp	= 0;
}
/////////////////////////////////////////////////////////////////////////////
CPresentMonDataProviderThread::~CPresentMonDataProviderThread()
{
}
/////////////////////////////////////////////////////////////////////////////
void CPresentMonDataProviderThread::StopConsoleStreaming(CPresentMonConsoleWrapper* lpConsoleWrapper)
{
	if (m_dwActiveProcessId)
	{
		lpConsoleWrapper->Destroy();

		m_dwActiveProcessId = 0;

		if (m_lpDataBuffer)
			m_lpDataBuffer->ResetFrameData();
	}
}
/////////////////////////////////////////////////////////////////////////////
void CPresentMonDataProviderThread::StartConsoleStreaming(DWORD dwTargetProcessId, CPresentMonConsoleWrapper* lpConsoleWrapper)
{
	StopConsoleStreaming(lpConsoleWrapper);

	if (dwTargetProcessId)
	{
		BOOL bResult = lpConsoleWrapper->Create(dwTargetProcessId);

		if (bResult)
		{
			APPEND_LOG1("Streaming frames for process %d\n", dwTargetProcessId);

			m_dwActiveProcessId = dwTargetProcessId;
		}
		else
		{
			APPEND_LOG1("Failed to start streaming frames for process %d\n", dwTargetProcessId);
		}

		if (m_lpDataBuffer)
			m_lpDataBuffer->SetStatus(bResult ? PMDP_STATUS_OK : PMDP_STATUS_START_STREAM_FAILED);
	}
}
/////////////////////////////////////////////////////////////////////////////
DWORD CPresentMonDataProviderThread::GetForegroundProcessId()
{
	DWORD dwProcessId = 0;

	if (m_dwTargetProcessId == 1)
		//current foreground process detection mode
	{
		HWND hWnd = GetForegroundWindow();

		if (hWnd)
		{
			if (IsFrameWindow(hWnd))
				hWnd = GetCoreWindow(hWnd);

			DWORD dwProcessID = 0;

			GetWindowThreadProcessId(hWnd, &dwProcessId);
		}
	}
	else
	if  (m_dwTargetProcessId == 0)
		//RTSS last 3D foreground process detection mode, this way we can use RTSS exclusions list and detect background 3D processes too
	{
		HANDLE hMapFile = OpenFileMapping(FILE_MAP_ALL_ACCESS, FALSE, "RTSSSharedMemoryV2");

		if (hMapFile)
		{
			LPVOID pMapAddr = MapViewOfFile(hMapFile, FILE_MAP_ALL_ACCESS, 0, 0, 0);

			LPRTSS_SHARED_MEMORY pMem = (LPRTSS_SHARED_MEMORY)pMapAddr;

			if (pMem)
			{
				if ((pMem->dwSignature == 'RTSS') && (pMem->dwVersion >= 0x00020000))
					dwProcessId = pMem->dwLastForegroundAppProcessID;

				UnmapViewOfFile(pMapAddr);
			}

			CloseHandle(hMapFile);
		}
	}

	return dwProcessId;
}
/////////////////////////////////////////////////////////////////////////////
DWORD CPresentMonDataProviderThread::GetForegroundProcessIdDelayed()
{
	DWORD dwTimestamp = GetTickCount();

	if (m_dwForegroundProcessTimestamp < dwTimestamp + 1000)
	{
		m_dwForegroundProcessId			= GetForegroundProcessId();
		m_dwForegroundProcessTimestamp	= dwTimestamp;
	}

	return m_dwForegroundProcessId;
}
/////////////////////////////////////////////////////////////////////////////
BOOL CPresentMonDataProviderThread::InitInstance()
{
	APPEND_LOG("Starting PresentMonDataProvider thread\n");

	if (m_lpDataBuffer)
		m_lpDataBuffer->CreateSharedMemory();

	DWORD dwConsoleMode = theWnd.GetConfigInt("Settings", "ConsoleMode", 0);

	if (dwConsoleMode == 2)	//forced mode (always use console instead of service)
		ConsoleConsumerLoop();
	else
	{
		if (!ConsumerLoop())
		{
			if (dwConsoleMode == 1)	//fallback mode (use console only if service is not available)
				ConsoleConsumerLoop();
		}
	}

	return FALSE;
}
/////////////////////////////////////////////////////////////////////////////
BOOL CPresentMonDataProviderThread::ConsoleConsumerLoop()
{
	APPEND_LOG("Creating PresentMon console wrapper\n");

	DWORD dwSleepPeriod	= theWnd.GetConfigInt("Settings", "SleepPeriod"	, 100	);
	DWORD dwBurstDelay	= theWnd.GetConfigInt("Settings", "BurstDelay"	, 3000	);

	PMDP_FRAME_DATA* lpFrameData = new PMDP_FRAME_DATA[PM_FRAME_DATA_MAX];

	CPresentMonConsoleWrapper* lpConsoleWrapper = new CPresentMonConsoleWrapper(theWnd.GetConfigStr("Settings", "ConsolePath", ""), theWnd.GetConfigStr("Settings", "ConsoleCmd", ""));

	if (m_lpDataBuffer)
		m_lpDataBuffer->SetStatus(PMDP_STATUS_OK);

	while (WaitForSingleObject(m_hEventKill, 0) != WAIT_OBJECT_0)
	{
		DWORD dwTargetProcessId = (m_dwTargetProcessId > 1) ? m_dwTargetProcessId : GetForegroundProcessIdDelayed();

		if (dwTargetProcessId != m_dwActiveProcessId)
		{
			StartConsoleStreaming(dwTargetProcessId, lpConsoleWrapper);

			Sleep(1000);
		}

		lpConsoleWrapper->Watchdog();

		BOOL bBurst = FALSE;

		if (m_dwActiveProcessId)
		{
			DWORD dwFrames = 0;

			do
			{
				dwFrames = lpConsoleWrapper->ConsumeFrames(lpFrameData, PM_FRAME_DATA_MAX);

				if (dwFrames)
				{
					if (m_lpDataBuffer)
						m_lpDataBuffer->SetFrameData(dwFrames, lpFrameData);

					if (GetFrameDelay(lpFrameData) > dwBurstDelay)
						//We consumed too old frame, which means that we're possibly consuming frames too slow causing reporting lag to grow.
						//In this case we'll enable burst mode and do not sleep on this loop iteration
						bBurst = TRUE;
				}
			} 
			while (dwFrames == PM_FRAME_DATA_MAX);
		}

		if (bBurst)
			SwitchToThread();
		else
			Sleep(dwSleepPeriod);
	}

	StopConsoleStreaming(lpConsoleWrapper);

	delete lpConsoleWrapper;

	delete[] lpFrameData;

	return TRUE;
}
/////////////////////////////////////////////////////////////////////////////
int CPresentMonDataProviderThread::ExitInstance()
{
	return CWinThread::ExitInstance();
}
/////////////////////////////////////////////////////////////////////////////
BEGIN_MESSAGE_MAP(CPresentMonDataProviderThread, CWinThread)
	//{{AFX_MSG_MAP(CPresentMonDataProviderThread)
		// NOTE - the ClassWizard will add and remove mapPresentMonDataProvider macros here.
	//}}AFX_MSG_MAP
END_MESSAGE_MAP()
/////////////////////////////////////////////////////////////////////////////
// CPresentMonDataProviderThread message handlers
/////////////////////////////////////////////////////////////////////////////
void CPresentMonDataProviderThread::Kill()
{
	SetEvent(m_hEventKill);
	SetThreadPriority(THREAD_PRIORITY_HIGHEST);

	WaitForSingleObject(m_hThread, INFINITE);

	Destroy();
}
/////////////////////////////////////////////////////////////////////////////
void CPresentMonDataProviderThread::Destroy()
{
	delete this;
}
/////////////////////////////////////////////////////////////////////////////
void CPresentMonDataProviderThread::SetTargetProcessId(DWORD dwProcessId)
{
	m_dwTargetProcessId = dwProcessId;
}
/////////////////////////////////////////////////////////////////////////////
BOOL CPresentMonDataProviderThread::IsFrameWindow(HWND hFrameWnd)
{
	char szClassName[MAX_PATH] = { 0 };

	if (!GetClassName(hFrameWnd, szClassName, sizeof(szClassName)))
		return FALSE;

	return strcmp(szClassName, "ApplicationFrameWindow") == 0;

}
//////////////////////////////////////////////////////////////////////
HWND CPresentMonDataProviderThread::GetCoreWindow(HWND hFrameWnd)
{
	DWORD dwFrameProcessID = 0;
	GetWindowThreadProcessId(hFrameWnd, &dwFrameProcessID);

	HWND hCoreWnd = FindWindowEx(hFrameWnd, NULL, NULL, NULL);

	while (hCoreWnd)
	{
		DWORD dwCoreProcessID = 0;
		GetWindowThreadProcessId(hCoreWnd, &dwCoreProcessID);

		if (dwCoreProcessID != dwFrameProcessID)
			return hCoreWnd;

		hCoreWnd = FindWindowEx(hFrameWnd, hCoreWnd, NULL, NULL);
	}

	return NULL;
}
//////////////////////////////////////////////////////////////////////
void CPresentMonDataProviderThread::StopStreaming(PM_SESSION_HANDLE session)
{
	if (m_dwActiveProcessId)
	{
		PM_STATUS err = pmStopTrackingProcess(session, m_dwActiveProcessId);

		m_dwActiveProcessId = 0;

		if (m_lpDataBuffer)
			m_lpDataBuffer->OnStopStream(err);
	}
}
/////////////////////////////////////////////////////////////////////////////
void CPresentMonDataProviderThread::StartStreaming(PM_SESSION_HANDLE session, DWORD dwTargetProcessId)
{
	StopStreaming(session);

	if (dwTargetProcessId)
	{
		PM_STATUS err = pmStartTrackingProcess(session, dwTargetProcessId);

		if (err == PM_STATUS_SUCCESS)
		{
			APPEND_LOG1("Streaming frames for process %d\n", dwTargetProcessId);

			m_dwActiveProcessId = dwTargetProcessId;
		}
		else
		{
			APPEND_LOG2("Failed to start streaming frames for process %d, error code %d\n", dwTargetProcessId, err);
		}

		if (m_lpDataBuffer)
			m_lpDataBuffer->OnStartStream(err);
	}
}
/////////////////////////////////////////////////////////////////////////////		
BOOL CPresentMonDataProviderThread::ConsumerLoop()
{
	PM_SESSION_HANDLE session;

	PM_STATUS err = pmOpenSession(&session);

	DWORD dwEtwFlushPeriod	= theWnd.GetConfigInt("Settings", "EtwFlushPeriod"	, 0);
	DWORD dwSleepPeriod		= theWnd.GetConfigInt("Settings", "SleepPeriod"		, 100);
	DWORD dwBurstDelay		= theWnd.GetConfigInt("Settings", "BurstDelay"		, 3000);

	pmSetEtwFlushPeriod(session, dwEtwFlushPeriod);

	if (m_lpDataBuffer)
		m_lpDataBuffer->OnInitialize(err);

	if (err == PM_STATUS_SUCCESS)
	{
		APPEND_LOG("Initializing PresentMon API V2\n");

		PM_QUERY_ELEMENT queryElements[]
		{
			{ PM_METRIC_SWAP_CHAIN_ADDRESS		, PM_STAT_NONE, 0, 0 },		//0		PM_DATA_TYPE_UINT64
			{ PM_METRIC_PRESENT_RUNTIME			, PM_STAT_NONE, 0, 0 },		//1		PM_DATA_TYPE_ENUM
			{ PM_METRIC_SYNC_INTERVAL			, PM_STAT_NONE, 0, 0 },		//2		PM_DATA_TYPE_INT32
			{ PM_METRIC_PRESENT_FLAGS			, PM_STAT_NONE, 0, 0 },		//3		PM_DATA_TYPE_UINT32
			{ PM_METRIC_ALLOWS_TEARING			, PM_STAT_NONE, 0, 0 },		//4		PM_DATA_TYPE_BOOL 
			{ PM_METRIC_PRESENT_MODE			, PM_STAT_NONE, 0, 0 },		//5		PM_DATA_TYPE_ENUM
			{ PM_METRIC_CPU_START_QPC			, PM_STAT_NONE, 0, 0 },		//6		PM_DATA_TYPE_UINT64
			{ PM_METRIC_CPU_FRAME_TIME			, PM_STAT_NONE, 0, 0 },		//7		PM_DATA_TYPE_DOUBLE
			{ PM_METRIC_CPU_BUSY				, PM_STAT_NONE, 0, 0 },		//8		PM_DATA_TYPE_DOUBLE
			{ PM_METRIC_CPU_WAIT				, PM_STAT_NONE, 0, 0 },		//9		PM_DATA_TYPE_DOUBLE
			{ PM_METRIC_GPU_LATENCY				, PM_STAT_NONE, 0, 0 },		//10	PM_DATA_TYPE_DOUBLE
			{ PM_METRIC_GPU_TIME				, PM_STAT_NONE, 0, 0 },		//11	PM_DATA_TYPE_DOUBLE
			{ PM_METRIC_GPU_BUSY				, PM_STAT_NONE, 0, 0 },		//12	PM_DATA_TYPE_DOUBLE
			{ PM_METRIC_GPU_WAIT				, PM_STAT_NONE, 0, 0 },		//13	PM_DATA_TYPE_DOUBLE
			{ PM_METRIC_DISPLAY_LATENCY			, PM_STAT_NONE, 0, 0 },		//14	PM_DATA_TYPE_DOUBLE
			{ PM_METRIC_DISPLAYED_TIME			, PM_STAT_NONE, 0, 0 },		//15	PM_DATA_TYPE_DOUBLE
			{ PM_METRIC_ANIMATION_ERROR			, PM_STAT_NONE, 0, 0 },		//16	PM_DATA_TYPE_DOUBLE
			{ PM_METRIC_CLICK_TO_PHOTON_LATENCY	, PM_STAT_NONE, 0, 0 },		//17	PM_DATA_TYPE_DOUBLE
		};

		PM_FRAME_QUERY_HANDLE frameQuery;

		uint32_t dwBlobSize = 0;

		err = pmRegisterFrameQuery(session, &frameQuery, queryElements, _countof(queryElements), &dwBlobSize);

		if (err == PM_STATUS_SUCCESS)
		{
			LPBYTE lpBlob = new BYTE[dwBlobSize * PM_FRAME_DATA_MAX];

			while (WaitForSingleObject(m_hEventKill, 0) != WAIT_OBJECT_0)
			{
				DWORD dwTargetProcessId = (m_dwTargetProcessId > 1) ? m_dwTargetProcessId : GetForegroundProcessIdDelayed();

				if (dwTargetProcessId != m_dwActiveProcessId)
				{
					StartStreaming(session, dwTargetProcessId);

					Sleep(1000);
				}

				BOOL bBurst = FALSE;

				if (m_dwActiveProcessId)
				{
					uint32_t dwFrames = PM_FRAME_DATA_MAX;

					do
					{
						err = pmConsumeFrames(frameQuery, m_dwActiveProcessId, lpBlob, &dwFrames);

						if (err == PM_STATUS_SUCCESS)
						{
							if (dwFrames)
							{
								if (m_lpDataBuffer)
								{
									PMDP_FRAME_DATA frame;
									ZeroMemory(&frame, sizeof(frame));

									for (DWORD dwFrame=0; dwFrame<dwFrames; dwFrame++)
									{
										LPBYTE lpFrameBlob = lpBlob + dwBlobSize * dwFrame;

										//PresentMon V1 metrics

										frame.data1.ProcessID				= m_dwActiveProcessId;

										frame.data1.SwapChainAddress		= GetQueryElementAsUint64(queryElements, 0	, lpFrameBlob, 0	);
										frame.data1.Runtime					= GetQueryElementAsUint32(queryElements, 1	, lpFrameBlob, 0	);
										frame.data1.SyncInterval			= GetQueryElementAsUint32(queryElements, 2	, lpFrameBlob, 0	);
										frame.data1.PresentFlags			= GetQueryElementAsUint32(queryElements, 3	, lpFrameBlob, 0	);
										frame.data1.AllowsTearing			= GetQueryElementAsByte  (queryElements, 4	, lpFrameBlob, 0	);
										frame.data1.PresentMode				= GetQueryElementAsUint32(queryElements, 5	, lpFrameBlob, 0	);

										//PresentMon V2 metrics

										frame.data2.CPUStart				= GetQueryElementAsUint64(queryElements, 6	, lpFrameBlob, 0	);
										frame.data2.Frametime				= GetQueryElementAsDouble(queryElements, 7	, lpFrameBlob, 0.0	);
										frame.data2.CPUBusy					= GetQueryElementAsDouble(queryElements, 8	, lpFrameBlob, 0.0	);
										frame.data2.CPUWait					= GetQueryElementAsDouble(queryElements, 9	, lpFrameBlob, 0.0	);
										frame.data2.GPULatency				= GetQueryElementAsDouble(queryElements, 10	, lpFrameBlob, 0.0	);
										frame.data2.GPUTime					= GetQueryElementAsDouble(queryElements, 11	, lpFrameBlob, 0.0	);
										frame.data2.GPUBusy					= GetQueryElementAsDouble(queryElements, 12	, lpFrameBlob, 0.0	);
										frame.data2.GPUWait					= GetQueryElementAsDouble(queryElements, 13	, lpFrameBlob, 0.0	);
										frame.data2.DisplayLatency			= GetQueryElementAsDouble(queryElements, 14	, lpFrameBlob, 0.0	);
										frame.data2.DisplayedTime			= GetQueryElementAsDouble(queryElements, 15	, lpFrameBlob, 0.0	);
										frame.data2.AnimationError			= GetQueryElementAsDouble(queryElements, 16	, lpFrameBlob, 0.0	);
										frame.data2.ClickToPhotonLatency	= GetQueryElementAsDouble(queryElements, 17	, lpFrameBlob, 0.0	);

										if (isnan(frame.data2.ClickToPhotonLatency))
											//ClickToPhotonLatency is event driven and may report no data, so we convert NaN to 0.0
											frame.data2.ClickToPhotonLatency = 0.0;

										//the next fields can be reported as NaN by PresentMon service for dropped frames, so we convert NaN to 0.0

										if (isnan(frame.data2.DisplayLatency))
											frame.data2.DisplayLatency = 0.0;

										if (isnan(frame.data2.DisplayedTime))
											frame.data2.DisplayedTime = 0.0;

										if (isnan(frame.data2.AnimationError))
											frame.data2.AnimationError = 0.0;

										frame.data1.qpcTime					= frame.data2.CPUStart;

										m_lpDataBuffer->SetFrameData(1, &frame);

										if (GetFrameDelay(&frame) > dwBurstDelay)
											//We consumed too old frame, which means that we're possibly consuming frames too slow causing reporting lag to grow.
											//In this case we'll enable burst mode and do not sleep on this loop iteration
											bBurst = TRUE;
									}
								}
							}
						}
						else
							break;
					} 
					while (dwFrames == PM_FRAME_DATA_MAX);

					if (m_lpDataBuffer)
						m_lpDataBuffer->OnGetFrameData(err);
				}

				if (bBurst)
					SwitchToThread();
				else
					Sleep(dwSleepPeriod);
			}

			StopStreaming(session);

			delete[] lpBlob;

			pmFreeFrameQuery(frameQuery);
		}
		else
		{
			APPEND_LOG1("Failed to register frame query, error code %d\n", err);

			pmCloseSession(session);

			return FALSE;
		}

		pmCloseSession(session);
	}
	else
	{
		APPEND_LOG1("Failed to initialize PresentMon API V2, error code %d\n", err);

		return FALSE;
	}

	return TRUE;
}
/////////////////////////////////////////////////////////////////////////////
BYTE CPresentMonDataProviderThread::GetQueryElementAsByte(PM_QUERY_ELEMENT* lpElements, DWORD dwIndex, LPBYTE lpBlob, BYTE defaultValue)
{
	if (lpElements[dwIndex].dataSize == sizeof(BYTE))
		return *(LPBYTE)(lpBlob + lpElements[dwIndex].dataOffset);

	return defaultValue;
}
/////////////////////////////////////////////////////////////////////////////
uint32_t CPresentMonDataProviderThread::GetQueryElementAsUint32(PM_QUERY_ELEMENT* lpElements, DWORD dwIndex, LPBYTE lpBlob, uint32_t defaultValue)
{
	if (lpElements[dwIndex].dataSize == sizeof(uint32_t))
		return *(uint32_t*)(lpBlob + lpElements[dwIndex].dataOffset);

	return defaultValue;
}
/////////////////////////////////////////////////////////////////////////////
uint64_t CPresentMonDataProviderThread::GetQueryElementAsUint64(PM_QUERY_ELEMENT* lpElements, DWORD dwIndex, LPBYTE lpBlob, uint64_t defaultValue)
{
	if (lpElements[dwIndex].dataSize == sizeof(uint64_t))
		return *(uint64_t*)(lpBlob + lpElements[dwIndex].dataOffset);

	return defaultValue;
}
/////////////////////////////////////////////////////////////////////////////
double CPresentMonDataProviderThread::GetQueryElementAsDouble(PM_QUERY_ELEMENT* lpElements, DWORD dwIndex, LPBYTE lpBlob, double defaultValue)
{
	if (lpElements[dwIndex].dataSize == sizeof(double))
		return *(double*)(lpBlob + lpElements[dwIndex].dataOffset);

	return defaultValue;
}
/////////////////////////////////////////////////////////////////////////////
DWORD CPresentMonDataProviderThread::GetFrameDelay(PMDP_FRAME_DATA* lpFrame)
{
	LARGE_INTEGER pf;
	QueryPerformanceFrequency(&pf);

	if (pf.QuadPart)
	{
		LARGE_INTEGER pc;
		QueryPerformanceCounter(&pc);

		return (DWORD)(1000.0 * (pc.QuadPart - lpFrame->data1.qpcTime) / pf.QuadPart);
	}

	return 0;
}
/////////////////////////////////////////////////////////////////////////////

