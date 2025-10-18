#include "pch.h"
#include "PresentMonConsoleWrapper.h"
#include "PresentMonDataBuffer.h"
#include "TokenString.h"
/////////////////////////////////////////////////////////////////////////////
#define SAFE_CLOSE_HANDLE(h) { if (h) { CloseHandle(h); h= 0; } } 
/////////////////////////////////////////////////////////////////////////////
#define PMCW_CID_Application				1
#define PMCW_CID_ProcessID					2
#define PMCW_CID_SwapChainAddress			3
#define PMCW_CID_Runtime					4
#define PMCW_CID_SyncInterval				5
#define PMCW_CID_PresentFlags				6
#define PMCW_CID_Dropped					7
#define PMCW_CID_TimeInSeconds				8
#define PMCW_CID_msInPresentAPI				9
#define PMCW_CID_msBetweenPresents			10
#define PMCW_CID_AllowsTearing				11
#define PMCW_CID_PresentMode				12
#define PMCW_CID_msUntilRenderComplete		13
#define PMCW_CID_msUntilDisplayed			14
#define PMCW_CID_msBetweenDisplayChange		15
#define PMCW_CID_msUntilRenderStart			16
#define PMCW_CID_msGPUActive				17
#define PMCW_CID_msSinceInput				18
#define PMCW_CID_QPCTime					19

#define PMCW_CID_PresentRuntime				20
#define PMCW_CID_CPUStartQPC				21
#define PMCW_CID_Frametime					22
#define PMCW_CID_CPUBusy					23
#define PMCW_CID_CPUWait					24
#define PMCW_CID_GPULatency					25
#define PMCW_CID_GPUTime					26
#define PMCW_CID_GPUBusy					27
#define PMCW_CID_GPUWait					28
#define PMCW_CID_DisplayLatency				29
#define PMCW_CID_DisplayedTime				30
#define PMCW_CID_AnimationError				31
#define PMCW_CID_ClickToPhotonLatency		32
#define PMCW_CID_AllInputToPhotonLatency	33

#define PMCW_CID_TimeInQPC					34
#define PMCW_CID_MsBetweenSimulationStart	35
#define PMCW_CID_MsRenderPresentLatency		36
#define PMCW_CID_MsPCLatency				37
#define PMCW_CID_MsBetweenAppStart			38		
#define PMCW_CID_MsCPUBusy					39
#define PMCW_CID_MsCPUWait					40
#define PMCW_CID_MsGPULatency				41
#define PMCW_CID_MsGPUTime					42
#define PMCW_CID_MsGPUBusy					43
#define PMCW_CID_MsGPUWait					44
#define PMCW_CID_MsAnimationError			45
#define PMCW_CID_AnimationTime				46
#define PMCW_CID_MsAllInputToPhotonLatency	47
#define PMCW_CID_MsClickToPhotonLatency		48
/////////////////////////////////////////////////////////////////////////////
typedef struct PMCW_COLUMN_DESC
{
	LPCSTR	lpName;
	DWORD	dwID;
} PMCW_COLUMN_DESC, *LPPMCW_COLUMN_DESC;
/////////////////////////////////////////////////////////////////////////////
PMCW_COLUMN_DESC g_columnsMap[] = {
	//v1.x.x
	{ "Application"					, PMCW_CID_Application				},
	{ "ProcessID"					, PMCW_CID_ProcessID				},
	{ "SwapChainAddress"			, PMCW_CID_SwapChainAddress			},
	{ "Runtime"						, PMCW_CID_Runtime					},
	{ "SyncInterval"				, PMCW_CID_SyncInterval				},
	{ "PresentFlags"				, PMCW_CID_PresentFlags				},
	{ "Dropped"						, PMCW_CID_Dropped					},
	{ "TimeInSeconds"				, PMCW_CID_TimeInSeconds			},
	{ "msInPresentAPI"				, PMCW_CID_msInPresentAPI			},
	{ "msBetweenPresents"			, PMCW_CID_msBetweenPresents		},
	{ "AllowsTearing"				, PMCW_CID_AllowsTearing			},
	{ "PresentMode"					, PMCW_CID_PresentMode				},
	{ "msUntilRenderComplete"		, PMCW_CID_msUntilRenderComplete	},
	{ "msUntilDisplayed"			, PMCW_CID_msUntilDisplayed			},
	{ "msBetweenDisplayChange"		, PMCW_CID_msBetweenDisplayChange	},
	{ "msUntilRenderStart"			, PMCW_CID_msUntilRenderStart		},
	{ "msGPUActive"					, PMCW_CID_msGPUActive				},
	{ "msSinceInput"				, PMCW_CID_msSinceInput				},
	{ "QPCTime"						, PMCW_CID_QPCTime					},
	//v2.0.0 and newer
	{ "PresentRuntime"				, PMCW_CID_PresentRuntime			},
	{ "CPUStartQPC"					, PMCW_CID_CPUStartQPC				},
	{ "FrameTime"					, PMCW_CID_Frametime				},
	{ "CPUBusy"						, PMCW_CID_CPUBusy					},
	{ "CPUWait"						, PMCW_CID_CPUWait					},
	{ "GPULatency"					, PMCW_CID_GPULatency				},
	{ "GPUTime"						, PMCW_CID_GPUTime					},
	{ "GPUBusy"						, PMCW_CID_GPUBusy					},
	{ "GPUWait"						, PMCW_CID_GPUWait					},
	{ "DisplayLatency"				, PMCW_CID_DisplayLatency			},
	{ "DisplayedTime"				, PMCW_CID_DisplayedTime			},
	{ "AnimationError"				, PMCW_CID_AnimationError			},
	{ "ClickToPhotonLatency"		, PMCW_CID_ClickToPhotonLatency		},
	{ "AllInputToPhotonLatency"		, PMCW_CID_AllInputToPhotonLatency	},
	//v2.3.1 and newer
	{ "TimeInQPC"					, PMCW_CID_TimeInQPC				},
	{ "MsBetweenSimulationStart"	, PMCW_CID_MsBetweenSimulationStart },
	{ "MsRenderPresentLatency"		, PMCW_CID_MsRenderPresentLatency	},
	{ "MsPCLatency"					, PMCW_CID_MsPCLatency				},
	{ "MsBetweenAppStart"			, PMCW_CID_MsBetweenAppStart		},
	{ "MsCPUBusy"					, PMCW_CID_MsCPUBusy				},
	{ "MsCPUWait"					, PMCW_CID_MsCPUWait				},
	{ "MsGPULatency"				, PMCW_CID_MsGPULatency				},
	{ "MsGPUTime"					, PMCW_CID_MsGPUTime				},
	{ "MsGPUBusy"					, PMCW_CID_MsGPUBusy				},
	{ "MsGPUWait"					, PMCW_CID_MsGPUWait				},
	{ "MsAnimationError"			, PMCW_CID_MsAnimationError			},
	{ "AnimationTime"				, PMCW_CID_AnimationTime			},
	{ "MsAllInputToPhotonLatency"	, PMCW_CID_MsAllInputToPhotonLatency},
	{ "MsClickToPhotonLatency"		, PMCW_CID_MsClickToPhotonLatency	}
};
/////////////////////////////////////////////////////////////////////////////
CPresentMonConsoleWrapper::CPresentMonConsoleWrapper(LPCSTR lpPath, LPCSTR lpCmd)
{
	m_strPath			= lpPath;
	m_strCmd			= lpCmd;
	m_dwVersionMajor	= 1;
	m_dwVersionMinor	= 0;
	m_dwVersionPatch	= 0;
	m_dwProcessId		= 0;
	m_dwTimestamp		= 0;

	m_hProcess			= NULL;
	m_hThread			= NULL;
	m_hStdOutRead		= NULL;
	m_hStdOutWrite		= NULL;

	sscanf_s(m_strPath, "PresentMon-%d.%d.%d", &m_dwVersionMajor, &m_dwVersionMinor, &m_dwVersionPatch);

	m_strLine			= "";

	if (m_dwVersionMajor >= 2)
	{
		m_bV1_Metrics = (strstr(m_strCmd, "-v1_metrics"		) != NULL);

		if (IsVersionGreaterOrEqual(2, 3, 1))
			m_bV2_Metrics = (strstr(m_strCmd, "-v2_metrics") != NULL);
		else
			m_bV2_Metrics = TRUE;

		m_bTrackInput = (strstr(m_strCmd, "-no_track_input"	) == NULL);
	}
	else
	{
		m_bV1_Metrics = TRUE;
		m_bV2_Metrics = FALSE;
		m_bTrackInput = (strstr(m_strCmd, "-track_input"	) != NULL);
	}
}
/////////////////////////////////////////////////////////////////////////////
CPresentMonConsoleWrapper::~CPresentMonConsoleWrapper()
{
	Destroy();
}
/////////////////////////////////////////////////////////////////////////////
BOOL CPresentMonConsoleWrapper::Create(DWORD dwProcessId)
{
	while (GetTickCount() < m_dwTimestamp + 5000)
		//Attempt to spawn new instance of console application while the previous is initializing causes it to fail to start, so give the previous spawned instance at least 5 seconds to 
		//finish initialization before spawning new one
		Sleep(0);

	Destroy();

	SECURITY_ATTRIBUTES sa;
	sa.nLength				= sizeof(SECURITY_ATTRIBUTES);
	sa.bInheritHandle		= TRUE;
	sa.lpSecurityDescriptor	= NULL;

	if (!CreatePipe(&m_hStdOutRead, &m_hStdOutWrite, &sa, 0))
		return FALSE;
	if (!SetHandleInformation(m_hStdOutRead, HANDLE_FLAG_INHERIT, 0))
		return FALSE;

	STARTUPINFO	si;
	ZeroMemory(&si, sizeof(STARTUPINFO));
	si.cb			= sizeof(STARTUPINFO);
	si.hStdError	= m_hStdOutWrite;
	si.hStdOutput	= m_hStdOutWrite;
	si.dwFlags		= STARTF_USESTDHANDLES;

	PROCESS_INFORMATION pi;
	ZeroMemory(&pi, sizeof(PROCESS_INFORMATION));

	char szCmdLine[MAX_PATH];

	if (m_dwVersionMajor >= 2)
		sprintf_s(szCmdLine, "%s -session_name PresentMonDataProvider -stop_existing_session -process_id %d -output_stdout -qpc_time", m_strPath.GetBuffer(), dwProcessId);
	else
		sprintf_s(szCmdLine, "%s -session_name PresentMonDataProvider -stop_existing_session -process_id %d -output_stdout -track_gpu -qpc_time", m_strPath.GetBuffer(), dwProcessId);

	if (!m_strCmd.IsEmpty())
	{
		strcat_s(szCmdLine, sizeof(szCmdLine), " "		);
		strcat_s(szCmdLine, sizeof(szCmdLine), m_strCmd	);
	}

	if (CreateProcess(NULL, szCmdLine, NULL, NULL, TRUE, CREATE_NO_WINDOW, NULL, NULL, &si, &pi))
	{
		m_hProcess		= pi.hProcess;
		m_hThread		= pi.hThread;
		m_dwProcessId	= dwProcessId;
		m_dwTimestamp	= GetTickCount();

		APPEND_LOG1("Spawning %s", szCmdLine);

		return TRUE;
	}

	APPEND_LOG1("Failed to spawn %s\n", szCmdLine);

	return FALSE;
}
/////////////////////////////////////////////////////////////////////////////
void CPresentMonConsoleWrapper::Destroy()
{
	if (m_hProcess)
	{
		//Attempt to kill the console application with TerminateProcess corrupts service functionality, 
		//so we need to close it properly by spawning new instance with termination command line switch

		STARTUPINFO	si;
		ZeroMemory(&si, sizeof(STARTUPINFO));
		si.cb = sizeof(STARTUPINFO);

		PROCESS_INFORMATION pi;
		ZeroMemory(&pi, sizeof(PROCESS_INFORMATION));

		char szCmdLine[MAX_PATH];

		if (m_dwVersionMajor >= 2)
			sprintf_s(szCmdLine, "%s -session_name PresentMonDataProvider -terminate_existing_session", m_strPath.GetBuffer());
		else
			sprintf_s(szCmdLine, "%s -session_name PresentMonDataProvider -terminate_existing", m_strPath.GetBuffer());

		if (CreateProcess(NULL, szCmdLine, NULL, NULL, TRUE, CREATE_NO_WINDOW, NULL, NULL, &si, &pi))
		{
			WaitForSingleObject(pi.hProcess, 5000);
				//Ensure that we don't try to spawn new session while the previous one is still terminating 

			CloseHandle(pi.hProcess);
			CloseHandle(pi.hThread);

			APPEND_LOG1("Spawning %s\n", szCmdLine);
		}
		else
		{
			APPEND_LOG1("Failed to spawn %s\n", szCmdLine);
		}
	}

	SAFE_CLOSE_HANDLE(m_hProcess);
	SAFE_CLOSE_HANDLE(m_hThread);
	SAFE_CLOSE_HANDLE(m_hStdOutRead);
	SAFE_CLOSE_HANDLE(m_hStdOutWrite);
}
/////////////////////////////////////////////////////////////////////////////
DWORD CPresentMonConsoleWrapper::ConsumeFrames(PMDP_FRAME_DATA* lpFrames, DWORD dwMaxFrames)
{
	DWORD dwFrames	= 0;
	DWORD dwTotal	= 0;

	if (PeekNamedPipe(m_hStdOutRead, NULL, 0, NULL, &dwTotal, NULL))
	{
		if (dwTotal)
		{
			LPBYTE lpBuffer = new BYTE[dwTotal];

			DWORD dwRead = 0;

			if (ReadFile(m_hStdOutRead, lpBuffer, dwTotal, &dwRead, NULL))
			{
				if (dwRead)
					dwFrames = ParseFramesBatch(lpBuffer, dwRead, lpFrames, dwMaxFrames);
			}

			delete[] lpBuffer;
		}
	}

	return dwFrames;
}
/////////////////////////////////////////////////////////////////////////////
DWORD CPresentMonConsoleWrapper::ParseFramesBatch(LPBYTE lpBuffer, DWORD dwSize, PMDP_FRAME_DATA* lpFrames, DWORD dwMaxFrames)
{
	DWORD dwFrames = 0;

	while (dwSize)
	{
		char c = *lpBuffer;

		if (c == 0x0D)		//CR
		{
			if (ParseFrame(m_strLine, lpFrames))
			{
				if (dwFrames < dwMaxFrames)
				{
					dwFrames++;
					lpFrames++;
				}
				else
					return dwFrames;
			}

			m_strLine = "";
		}
		else
		if (c == 0x0A)		//LF
		{
		}
		else
			m_strLine += c;

		lpBuffer++;
		dwSize--;
	}

	return dwFrames;
}
/////////////////////////////////////////////////////////////////////////////
BOOL CPresentMonConsoleWrapper::ParseFrame(LPCSTR lpLine, PMDP_FRAME_DATA* lpFrame)
{
	BOOL bResult = FALSE;

	memset(lpFrame, 0, sizeof(PMDP_FRAME_DATA));

	DWORD dwIndex = 0;

	CTokenString ts;

	LPSTR lpToken = ts.strtok(lpLine, ",");

	if (lpToken)
	{
		if (!_stricmp(lpToken, "Application"))
			InitColumnsMap(lpLine);
	}

	if (m_bV1_Metrics)
		//v1 layout
	{
		while (lpToken)
		{
			switch (GetMappedColumn(dwIndex))
			{
			case PMCW_CID_Application:
				sscanf_s(lpToken, "%s", &lpFrame->data1.Application, (UINT)sizeof(PM_FRAME_DATA_V1::Application));
				break;
			case PMCW_CID_ProcessID:
				if (sscanf_s(lpToken, "%d", &lpFrame->data1.ProcessID) == 1)
				{
					if (lpFrame->data1.ProcessID == m_dwProcessId)
						bResult = TRUE;
				}
				break;
			case PMCW_CID_SwapChainAddress:
				sscanf_s(lpToken, "0x%llX", &lpFrame->data1.SwapChainAddress);
				break;
			case PMCW_CID_Runtime:
				lpFrame->data1.Runtime = EncodeRuntime(lpToken);
				break;
			case PMCW_CID_SyncInterval:
				sscanf_s(lpToken, "%d", &lpFrame->data1.SyncInterval);
				break;
			case PMCW_CID_PresentFlags:
				sscanf_s(lpToken, "%d", &lpFrame->data1.PresentFlags);
				break;
			case PMCW_CID_Dropped:
				sscanf_s(lpToken, "%d", &lpFrame->data1.Dropped);
				break;
			case PMCW_CID_TimeInSeconds:
				sscanf_s(lpToken, "%lf", &lpFrame->data1.TimeInSeconds);
				break;
			case PMCW_CID_msInPresentAPI:
				sscanf_s(lpToken, "%lf", &lpFrame->data1.msInPresentAPI);
				break;
			case PMCW_CID_msBetweenPresents:
				sscanf_s(lpToken, "%lf", &lpFrame->data1.msBetweenPresents);
				break;
			case PMCW_CID_AllowsTearing:
				sscanf_s(lpToken, "%d", &lpFrame->data1.AllowsTearing);
				break;
			case PMCW_CID_PresentMode:
				lpFrame->data1.PresentMode = EncodePresentMode(lpToken);
				break;
			case PMCW_CID_msUntilRenderComplete:
				sscanf_s(lpToken, "%lf", &lpFrame->data1.msUntilRenderComplete);
				break;
			case PMCW_CID_msUntilDisplayed:
				sscanf_s(lpToken, "%lf", &lpFrame->data1.msUntilDisplayed);
				break;
			case PMCW_CID_msBetweenDisplayChange:
				sscanf_s(lpToken, "%lf", &lpFrame->data1.msBetweenDisplayChange);
				break;
			case PMCW_CID_msUntilRenderStart:
				sscanf_s(lpToken, "%lf", &lpFrame->data1.msUntilRenderStart);
				break;
			case PMCW_CID_msGPUActive:
				sscanf_s(lpToken, "%lf", &lpFrame->data1.msGpuActive);
				break;
			case PMCW_CID_msSinceInput:
				sscanf_s(lpToken, "%lf", &lpFrame->data1.msSinceInput);
				break;
			case PMCW_CID_QPCTime:
				sscanf_s(lpToken, "%lld", &lpFrame->data1.qpcTime);
				break;
			}

			lpToken = ts.strtok(NULL, ",");

			dwIndex++;
		}
	}
	else
	if (m_bV2_Metrics)
		//v2 layout
	{
		if ((m_dwVersionMajor == 2) &&
			(m_dwVersionMinor == 0))
			//v2.0 layout
		{
			while (lpToken)
			{
				switch (GetMappedColumn(dwIndex))
				{
				case PMCW_CID_Application:
					sscanf_s(lpToken, "%s", &lpFrame->data1.Application, (UINT)sizeof(PM_FRAME_DATA_V1::Application));
					break;
				case PMCW_CID_ProcessID:
					if (sscanf_s(lpToken, "%d", &lpFrame->data1.ProcessID) == 1)
					{
						if (lpFrame->data1.ProcessID == m_dwProcessId)
							bResult = TRUE;
					}
					break;
				case PMCW_CID_SwapChainAddress:
					sscanf_s(lpToken, "0x%llX", &lpFrame->data1.SwapChainAddress);
					break;
				case PMCW_CID_Runtime:
					lpFrame->data1.Runtime = EncodeRuntime(lpToken);
					break;
				case PMCW_CID_SyncInterval:
					sscanf_s(lpToken, "%d", &lpFrame->data1.SyncInterval);
					break;
				case PMCW_CID_PresentFlags:
					sscanf_s(lpToken, "%d", &lpFrame->data1.PresentFlags);
					break;
				case PMCW_CID_AllowsTearing:
					sscanf_s(lpToken, "%d", &lpFrame->data1.AllowsTearing);
					break;
				case PMCW_CID_PresentMode:
					lpFrame->data1.PresentMode = EncodePresentMode(lpToken);
					break;
				case PMCW_CID_CPUStartQPC:
					sscanf_s(lpToken, "%lld", &lpFrame->data2.CPUStart);
					break;
				case PMCW_CID_CPUBusy:
					sscanf_s(lpToken, "%lf", &lpFrame->data2.CPUBusy);
					break;
				case PMCW_CID_CPUWait:
					sscanf_s(lpToken, "%lf", &lpFrame->data2.CPUWait);
					break;
				case PMCW_CID_GPULatency:
					sscanf_s(lpToken, "%lf", &lpFrame->data2.GPULatency);
					break;
				case PMCW_CID_GPUBusy:
					sscanf_s(lpToken, "%lf", &lpFrame->data2.GPUBusy);
					break;
				case PMCW_CID_GPUWait:
					sscanf_s(lpToken, "%lf", &lpFrame->data2.GPUWait);
					break;
				case PMCW_CID_DisplayLatency:
					sscanf_s(lpToken, "%lf", &lpFrame->data2.DisplayLatency);
					break;
				case PMCW_CID_DisplayedTime:
					sscanf_s(lpToken, "%lf", &lpFrame->data2.DisplayedTime);
					break;
				case PMCW_CID_ClickToPhotonLatency:
					sscanf_s(lpToken, "%lf", &lpFrame->data2.ClickToPhotonLatency);
					break;
				}

				lpFrame->data1.qpcTime		= lpFrame->data2.CPUStart;
				lpFrame->data2.Frametime	= lpFrame->data2.CPUBusy + lpFrame->data2.CPUWait;
				lpFrame->data2.GPUTime		= lpFrame->data2.GPUBusy + lpFrame->data2.GPUWait;

				lpToken = ts.strtok(NULL, ",");

				dwIndex++;
			}
		}
		else
			//v2.1+ layout
		{
			while (lpToken)
			{
				switch (GetMappedColumn(dwIndex))
				{
				case PMCW_CID_Application:
					sscanf_s(lpToken, "%s", &lpFrame->data1.Application, (UINT)sizeof(PM_FRAME_DATA_V1::Application));
					break;
				case PMCW_CID_ProcessID:
					if (sscanf_s(lpToken, "%d", &lpFrame->data1.ProcessID) == 1)
					{
						if (lpFrame->data1.ProcessID == m_dwProcessId)
							bResult = TRUE;
					}
					break;
				case PMCW_CID_SwapChainAddress:
					sscanf_s(lpToken, "0x%llX", &lpFrame->data1.SwapChainAddress);
					break;
				case PMCW_CID_PresentRuntime:
					lpFrame->data1.Runtime = EncodeRuntime(lpToken);
					break;
				case PMCW_CID_SyncInterval:
					sscanf_s(lpToken, "%d", &lpFrame->data1.SyncInterval);
					break;
				case PMCW_CID_PresentFlags:
					sscanf_s(lpToken, "%d", &lpFrame->data1.PresentFlags);
					break;
				case PMCW_CID_AllowsTearing:
					sscanf_s(lpToken, "%d", &lpFrame->data1.AllowsTearing);
					break;
				case PMCW_CID_PresentMode:
					lpFrame->data1.PresentMode = EncodePresentMode(lpToken);
					break;
				case PMCW_CID_CPUStartQPC:
					sscanf_s(lpToken, "%lld", &lpFrame->data2.CPUStart);
					break;
				case PMCW_CID_Frametime:
					sscanf_s(lpToken, "%lf", &lpFrame->data2.Frametime);
					break;
				case PMCW_CID_CPUBusy:
					sscanf_s(lpToken, "%lf", &lpFrame->data2.CPUBusy);
					break;
				case PMCW_CID_CPUWait:
					sscanf_s(lpToken, "%lf", &lpFrame->data2.CPUWait);
					break;
				case PMCW_CID_GPULatency:
					sscanf_s(lpToken, "%lf", &lpFrame->data2.GPULatency);
					break;
				case PMCW_CID_GPUTime:
					sscanf_s(lpToken, "%lf", &lpFrame->data2.GPUTime);
					break;
				case PMCW_CID_GPUBusy:
					sscanf_s(lpToken, "%lf", &lpFrame->data2.GPUBusy);
					break;
				case PMCW_CID_GPUWait:
					sscanf_s(lpToken, "%lf", &lpFrame->data2.GPUWait);
					break;
				case PMCW_CID_DisplayLatency:
					sscanf_s(lpToken, "%lf", &lpFrame->data2.DisplayLatency);
					break;
				case PMCW_CID_DisplayedTime:
					sscanf_s(lpToken, "%lf", &lpFrame->data2.DisplayedTime);
					break;
				case PMCW_CID_AnimationError:
					sscanf_s(lpToken, "%lf", &lpFrame->data2.AnimationError);
					break;
				case PMCW_CID_ClickToPhotonLatency:
					sscanf_s(lpToken, "%lf", &lpFrame->data2.ClickToPhotonLatency);
					break;
				case PMCW_CID_AllInputToPhotonLatency:
					//unused
					break;
				}

				lpFrame->data1.qpcTime = lpFrame->data2.CPUStart;

				lpToken = ts.strtok(NULL, ",");

				dwIndex++;
			}
		}
	}
	else
		//combined v1/v2 layout introduced by v2.3.1
	{
		while (lpToken)
		{
			switch (GetMappedColumn(dwIndex))
			{
			case PMCW_CID_Application:
				sscanf_s(lpToken, "%s", &lpFrame->data1.Application, (UINT)sizeof(PM_FRAME_DATA_V1::Application));
				break;
			case PMCW_CID_ProcessID:
				if (sscanf_s(lpToken, "%d", &lpFrame->data1.ProcessID) == 1)
				{
					if (lpFrame->data1.ProcessID == m_dwProcessId)
						bResult = TRUE;
				}
				break;
			case PMCW_CID_SwapChainAddress:
				sscanf_s(lpToken, "0x%llX", &lpFrame->data1.SwapChainAddress);
				break;
			case PMCW_CID_PresentRuntime:
				lpFrame->data1.Runtime = EncodeRuntime(lpToken);
				break;
			case PMCW_CID_SyncInterval:
				sscanf_s(lpToken, "%d", &lpFrame->data1.SyncInterval);
				break;
			case PMCW_CID_PresentFlags:
				sscanf_s(lpToken, "%d", &lpFrame->data1.PresentFlags);
				break;
			case PMCW_CID_AllowsTearing:
				sscanf_s(lpToken, "%d", &lpFrame->data1.AllowsTearing);
				break;
			case PMCW_CID_PresentMode:
				lpFrame->data1.PresentMode = EncodePresentMode(lpToken);
				break;
			case PMCW_CID_TimeInQPC:
				sscanf_s(lpToken, "%lld", &lpFrame->data1.qpcTime);
				break;
			case PMCW_CID_MsBetweenSimulationStart:
				//reserved for future
				break;
			case PMCW_CID_msBetweenPresents:
				sscanf_s(lpToken, "%lf", &lpFrame->data1.msBetweenPresents);
				break;
			case PMCW_CID_msBetweenDisplayChange:
				sscanf_s(lpToken, "%lf", &lpFrame->data1.msBetweenDisplayChange);
				break;
			case PMCW_CID_msInPresentAPI:
				sscanf_s(lpToken, "%lf", &lpFrame->data1.msInPresentAPI);
				break;
			case PMCW_CID_MsRenderPresentLatency:
				sscanf_s(lpToken, "%lf", &lpFrame->data1.msUntilRenderComplete);
				break;
			case PMCW_CID_msUntilDisplayed:
				sscanf_s(lpToken, "%lf", &lpFrame->data1.msUntilDisplayed);
				break;
			case PMCW_CID_MsPCLatency:
				//reserved for future
				break;
			case PMCW_CID_CPUStartQPC:
				sscanf_s(lpToken, "%lld", &lpFrame->data2.CPUStart);
				break;
			case PMCW_CID_MsBetweenAppStart:
				sscanf_s(lpToken, "%lf", &lpFrame->data2.Frametime);
				break;
			case PMCW_CID_MsCPUBusy:
				sscanf_s(lpToken, "%lf", &lpFrame->data2.CPUBusy);
				break;
			case PMCW_CID_MsCPUWait:
				sscanf_s(lpToken, "%lf", &lpFrame->data2.CPUWait);
				break;
			case PMCW_CID_MsGPULatency:
				sscanf_s(lpToken, "%lf", &lpFrame->data2.GPULatency);
				break;
			case PMCW_CID_MsGPUTime:
				sscanf_s(lpToken, "%lf", &lpFrame->data2.GPUTime);
				break;
			case PMCW_CID_MsGPUBusy:
				sscanf_s(lpToken, "%lf", &lpFrame->data2.GPUBusy);
				break;
			case PMCW_CID_MsGPUWait:
				sscanf_s(lpToken, "%lf", &lpFrame->data2.GPUWait);
				break;
			case PMCW_CID_MsAnimationError:
				sscanf_s(lpToken, "%lf", &lpFrame->data2.AnimationError);
				break;
			case PMCW_CID_AnimationTime:
				//unused
				break;
			case PMCW_CID_MsAllInputToPhotonLatency:
				//unused
				break;
			case PMCW_CID_MsClickToPhotonLatency:
				sscanf_s(lpToken, "%lf", &lpFrame->data2.ClickToPhotonLatency);
				break;
			}

			lpToken = ts.strtok(NULL, ",");

			dwIndex++;
		}
	}

	return bResult;
}
/////////////////////////////////////////////////////////////////////////////
void CPresentMonConsoleWrapper::Watchdog()
{
	if (m_hProcess)
	{
		if (WaitForSingleObject(m_hProcess, 0) == WAIT_OBJECT_0)
		{
			APPEND_LOG("Watchdog detected spawned console application termination, respawning");

			Create(m_dwProcessId);
		}
	}
}
/////////////////////////////////////////////////////////////////////////////
DWORD CPresentMonConsoleWrapper::GetVersionMajor()
{
	return m_dwVersionMajor;
}
/////////////////////////////////////////////////////////////////////////////
DWORD CPresentMonConsoleWrapper::GetVersionMinor()
{
	return m_dwVersionMinor;
}
/////////////////////////////////////////////////////////////////////////////
DWORD CPresentMonConsoleWrapper::GetVersionPatch()
{
	return m_dwVersionPatch;
}
/////////////////////////////////////////////////////////////////////////////
BOOL CPresentMonConsoleWrapper::IsVersionGreaterOrEqual(DWORD dwMajor, DWORD dwMinor, DWORD dwPatch)
{
	return ((m_dwVersionMajor << 16) | (m_dwVersionMinor << 8) | m_dwVersionPatch) >= ((dwMajor << 16) | (dwMinor << 8) | dwPatch);
}
/////////////////////////////////////////////////////////////////////////////
uint32_t CPresentMonConsoleWrapper::EncodeRuntime(LPCSTR lpString)
{
	if (!_stricmp(lpString, "DXGI"))
		return PM_GRAPHICS_RUNTIME_DXGI;
	else
	if (!_stricmp(lpString, "D3D9"))
		return PM_GRAPHICS_RUNTIME_D3D9;

	return PM_GRAPHICS_RUNTIME_UNKNOWN;
}
/////////////////////////////////////////////////////////////////////////////
uint32_t CPresentMonConsoleWrapper::EncodePresentMode(LPCSTR lpString)
{
		if (!_stricmp(lpString, "Hardware: Legacy Flip"))
			return PM_PRESENT_MODE_HARDWARE_LEGACY_FLIP;
		else
		if (!_stricmp(lpString, "Hardware: Legacy Copy to front buffer"))
			return PM_PRESENT_MODE_HARDWARE_LEGACY_COPY_TO_FRONT_BUFFER;
		else
		if (!_stricmp(lpString, "Hardware: Independent Flip"))
			return PM_PRESENT_MODE_HARDWARE_INDEPENDENT_FLIP;
		else
		if (!_stricmp(lpString, "Composed: Flip"))
			return PM_PRESENT_MODE_COMPOSED_FLIP;
		else
		if (!_stricmp(lpString, "Composed: Copy with GPU GDI"))
			return PM_PRESENT_MODE_COMPOSED_COPY_WITH_GPU_GDI;
		else
		if (!_stricmp(lpString, "Composed: Copy with CPU GDI"))
			return PM_PRESENT_MODE_COMPOSED_COPY_WITH_CPU_GDI;
		else
		if (!_stricmp(lpString, "Hardware Composed: Independent Flip"))
			return PM_PRESENT_MODE_HARDWARE_COMPOSED_INDEPENDENT_FLIP;

		return PM_PRESENT_MODE_UNKNOWN;
}
/////////////////////////////////////////////////////////////////////////////
void CPresentMonConsoleWrapper::InitColumnsMap(LPCSTR lpLine)
{
	m_columns.RemoveAll();

	CTokenString ts;

	LPSTR lpToken = ts.strtok(lpLine, ",");

	while (lpToken)
	{
		BOOL bMapped = FALSE;

		for (DWORD dwIndex=0; dwIndex<_countof(g_columnsMap); dwIndex++)
		{
			if (!_stricmp(lpToken, g_columnsMap[dwIndex].lpName))
			{
				m_columns.Add(g_columnsMap[dwIndex].dwID);

				bMapped = TRUE;

				break;
			}
		}

		if (!bMapped)
			m_columns.Add(0);
	
		lpToken = ts.strtok(NULL, ",");
	}
}
/////////////////////////////////////////////////////////////////////////////
DWORD CPresentMonConsoleWrapper::GetMappedColumn(DWORD dwIndex)
{
	if (dwIndex < m_columns.GetSize())
		return m_columns.GetAt(dwIndex);

	return 0;
}
/////////////////////////////////////////////////////////////////////////////
