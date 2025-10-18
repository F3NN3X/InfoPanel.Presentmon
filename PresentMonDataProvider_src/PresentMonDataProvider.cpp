// PresentMonDataProvider.cpp : Defines the class behaviors for the application.
//
// created by Unwinder
//////////////////////////////////////////////////////////////////////
#include "pch.h"
#include "framework.h"
#include "PresentMonDataProvider.h"
#include "PresentMonDataProviderCommandLineInfo.h"
#include "PresentMonDataProviderConnectWnd.h"
//////////////////////////////////////////////////////////////////////
#ifdef _DEBUG
#define new DEBUG_NEW
#endif
//////////////////////////////////////////////////////////////////////
// CPresentMonDataProviderApp
//////////////////////////////////////////////////////////////////////
BEGIN_MESSAGE_MAP(CPresentMonDataProviderApp, CWinApp)
	ON_COMMAND(ID_HELP, &CWinApp::OnHelp)
END_MESSAGE_MAP()
//////////////////////////////////////////////////////////////////////
// CPresentMonDataProviderApp construction
//////////////////////////////////////////////////////////////////////
CPresentMonDataProviderApp::CPresentMonDataProviderApp()
{
	m_dwRestartManagerSupportFlags = AFX_RESTART_MANAGER_SUPPORT_RESTART;
}
//////////////////////////////////////////////////////////////////////
// The one and only CPresentMonDataProviderApp object
//////////////////////////////////////////////////////////////////////
CPresentMonDataProviderApp			theApp;
CPresentMonDataProviderConnectWnd	theWnd;

HANDLE								g_hMapping = NULL;
//////////////////////////////////////////////////////////////////////
// CPresentMonDataProviderApp initialization
//////////////////////////////////////////////////////////////////////
BOOL CPresentMonDataProviderApp::InitInstance()
{
	CWinApp::InitInstance();

	CPresentMonDataProviderCommandLineInfo cmdLineInfo;
	ParseCommandLine(cmdLineInfo);

	HWND hWnd = FindWindow(NULL, CONNECT_WND_NAME);

	if (cmdLineInfo.IsUninstall())
	{
		if (hWnd)
		{
			APPEND_LOG("Uninstalling active instance of PresentMonDataProvider\n");

			PostMessage(hWnd, WM_QUIT, 0, 0);
		}

		return FALSE;
	}

	if (!cmdLineInfo.IsInstall())
	{
		if (hWnd)
			PostMessage(hWnd, UM_SET_TARGET_PROCESS, cmdLineInfo.GetProcessId(), 0);
		else
		{
			APPEND_LOG("PresentMonDataProvider installation is not requested, aborting\n");
		}

		return FALSE;
	}

	if (TestOnInstance())
	{
		if (hWnd)
			PostMessage(hWnd, UM_SET_TARGET_PROCESS, cmdLineInfo.GetProcessId(), 0);

		APPEND_LOG("PresentMonDataProvider is already active, aborting\n");

		return FALSE;
	}

	theWnd.SetTargetProcessId(cmdLineInfo.GetProcessId());

	if (!theWnd.CreateEx(0, AfxRegisterWndClass(0, ::LoadCursor(NULL, IDC_ARROW)), CONNECT_WND_NAME, WS_POPUP, 0, 0, 0, 0, NULL, NULL))
		return FALSE;

	m_pMainWnd = &theWnd;

	return TRUE;
}
//////////////////////////////////////////////////////////////////////
BOOL CPresentMonDataProviderApp::TestOnInstance()
{
	g_hMapping = ::CreateFileMapping((HANDLE)-1, NULL, PAGE_READONLY, 0, 32, "PresentMonDataProvider");

	if (g_hMapping)
		if (GetLastError() == ERROR_ALREADY_EXISTS)
			return TRUE;

	return FALSE;
}
//////////////////////////////////////////////////////////////////////
int CPresentMonDataProviderApp::ExitInstance()
{
	if (g_hMapping)
		CloseHandle(g_hMapping);

	theWnd.DestroyWindow();

	return CWinApp::ExitInstance();
}
//////////////////////////////////////////////////////////////////////
