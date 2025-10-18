// PresentMonDataProviderConnectWnd.cpp : implementation file
//
/////////////////////////////////////////////////////////////////////////////
#include "pch.h"
#include "Resource.h"
#include "PresentMonDataProviderConnectWnd.h"
/////////////////////////////////////////////////////////////////////////////
#ifdef _DEBUG
#define new DEBUG_NEW
#undef THIS_FILE
static char THIS_FILE[] = __FILE__;
#endif
/////////////////////////////////////////////////////////////////////////////
// CPresentMonDataProviderConnectWnd
/////////////////////////////////////////////////////////////////////////////
CPresentMonDataProviderConnectWnd::CPresentMonDataProviderConnectWnd()
{
	m_lpThread			= NULL;
	m_dwTargetProcessId	= 0;
}
/////////////////////////////////////////////////////////////////////////////
CPresentMonDataProviderConnectWnd::~CPresentMonDataProviderConnectWnd()
{
	DestroyThread();
}
/////////////////////////////////////////////////////////////////////////////
BEGIN_MESSAGE_MAP(CPresentMonDataProviderConnectWnd, CWnd)
	//{{AFX_MSG_MAP(CPresentMonDataProviderConnectWnd)
		// NOTE - the ClassWizard will add and remove mapping macros here.
	//}}AFX_MSG_MAP
	ON_WM_CREATE()
	ON_WM_DESTROY()
END_MESSAGE_MAP()
/////////////////////////////////////////////////////////////////////////////
// CPresentMonDataProviderConnectWnd message handlers
/////////////////////////////////////////////////////////////////////////////
LRESULT CPresentMonDataProviderConnectWnd::DefWindowProc(UINT message, WPARAM wParam, LPARAM lParam) 
{
	if (message == UM_SET_TARGET_PROCESS)
		SetTargetProcessId((DWORD)wParam);

	return CWnd::DefWindowProc(message, wParam, lParam);
}
/////////////////////////////////////////////////////////////////////////////
int CPresentMonDataProviderConnectWnd::OnCreate(LPCREATESTRUCT lpCreateStruct)
{
	if (CWnd::OnCreate(lpCreateStruct) == -1)
		return -1;

	CreateThread();

	return 0;
}
/////////////////////////////////////////////////////////////////////////////
void CPresentMonDataProviderConnectWnd::OnDestroy()
{
	DestroyThread();

	m_buffer.DestroySharedMemory();

	CWnd::OnDestroy();
}
/////////////////////////////////////////////////////////////////////////////
void CPresentMonDataProviderConnectWnd::CreateThread()
{
	DestroyThread();

	m_lpThread = new CPresentMonDataProviderThread(&m_buffer);
	m_lpThread->CreateThread(CREATE_SUSPENDED);
	m_lpThread->SetThreadPriority(THREAD_PRIORITY_TIME_CRITICAL);
	m_lpThread->ResumeThread();
	m_lpThread->SetTargetProcessId(m_dwTargetProcessId);
}
/////////////////////////////////////////////////////////////////////////////
void CPresentMonDataProviderConnectWnd::DestroyThread()
{
	if (m_lpThread)
		m_lpThread->Kill();
	m_lpThread = NULL;
}
/////////////////////////////////////////////////////////////////////////////
void CPresentMonDataProviderConnectWnd::SetTargetProcessId(DWORD dwProcessId)
{
	m_dwTargetProcessId = dwProcessId;

	if (m_lpThread)
		m_lpThread->SetTargetProcessId(dwProcessId);
}
/////////////////////////////////////////////////////////////////////////////
CString CPresentMonDataProviderConnectWnd::GetCfgPath()
{
	char szCfgPath[MAX_PATH];
	GetModuleFileName(NULL, szCfgPath, MAX_PATH);
	PathRenameExtension(szCfgPath, ".cfg");

	return szCfgPath;
}
/////////////////////////////////////////////////////////////////////////////
CString CPresentMonDataProviderConnectWnd::GetConfigStr(LPCSTR lpSection, LPCSTR lpName, LPCTSTR lpDefault)
{
	char szBuf[CHAR_BUF_SIZE];
	GetPrivateProfileString(lpSection, lpName, lpDefault, szBuf, CHAR_BUF_SIZE, GetCfgPath());

	return szBuf;
}
//////////////////////////////////////////////////////////////////////
void CPresentMonDataProviderConnectWnd::SetConfigStr(LPCSTR lpSection, LPCSTR lpName, LPCSTR lpValue)
{
	WritePrivateProfileString(lpSection, lpName, lpValue, GetCfgPath());
}
//////////////////////////////////////////////////////////////////////
int	CPresentMonDataProviderConnectWnd::GetConfigInt(LPCSTR lpSection, LPCSTR lpName, int nDefault)
{
	return GetPrivateProfileInt(lpSection, lpName, nDefault, GetCfgPath());
}
//////////////////////////////////////////////////////////////////////
void CPresentMonDataProviderConnectWnd::SetConfigInt(LPCSTR lpSection, LPCSTR lpName, int nValue)
{
	char szValue[MAX_PATH];
	sprintf_s(szValue, sizeof(szValue), "%d", nValue);

	WritePrivateProfileString(lpSection, lpName, szValue, GetCfgPath());
}
//////////////////////////////////////////////////////////////////////
DWORD CPresentMonDataProviderConnectWnd::GetConfigHex(LPCSTR lpSection, LPCSTR lpName, DWORD dwDefault)
{
	char szValue[MAX_PATH];
	GetPrivateProfileString(lpSection, lpName, "", szValue, MAX_PATH, GetCfgPath());

	DWORD dwResult = dwDefault;
	sscanf_s(szValue, "%08X", &dwResult);

	return dwResult;
}
//////////////////////////////////////////////////////////////////////
void CPresentMonDataProviderConnectWnd::SetConfigHex(LPCSTR lpSection, LPCSTR lpName, DWORD dwValue)
{
	char szValue[MAX_PATH];
	sprintf_s(szValue, sizeof(szValue), "%08X", dwValue);

	WritePrivateProfileString(lpSection, lpName, szValue, GetCfgPath());
}
//////////////////////////////////////////////////////////////////////
