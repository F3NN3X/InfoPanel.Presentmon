// PresentMonDataProviderCommandLineInfo.cpp: implementation of the CPresentMonDataProviderCommandLineInfo class.
//
// created by Unwinder
//////////////////////////////////////////////////////////////////////
#include "pch.h"
#include "PresentMonDataProviderCommandLineInfo.h"
//////////////////////////////////////////////////////////////////////
#ifdef _DEBUG
#undef THIS_FILE
static char THIS_FILE[]=__FILE__;
#define new DEBUG_NEW
#endif
//////////////////////////////////////////////////////////////////////
// Construction/Destruction
//////////////////////////////////////////////////////////////////////
CPresentMonDataProviderCommandLineInfo::CPresentMonDataProviderCommandLineInfo()
{
	m_bInstall			= FALSE;
	m_bUninstall		= FALSE;
	m_dwProcessId		= 0;
}
//////////////////////////////////////////////////////////////////////
CPresentMonDataProviderCommandLineInfo::~CPresentMonDataProviderCommandLineInfo()
{
}
//////////////////////////////////////////////////////////////////////
void CPresentMonDataProviderCommandLineInfo::ParseParam(LPCTSTR lpszParam, BOOL bFlag, BOOL /*bLast*/)
{
	if (bFlag)
	{
		if (!_stricmp(lpszParam, "i"))
			m_bInstall = TRUE;
		else
			if (!_stricmp(lpszParam, "u"))
				m_bUninstall = TRUE;
	}
	else
		sscanf_s(lpszParam, "%d", &m_dwProcessId);
}
//////////////////////////////////////////////////////////////////////
BOOL CPresentMonDataProviderCommandLineInfo::IsInstall()
{
	return m_bInstall;
}
//////////////////////////////////////////////////////////////////////
BOOL CPresentMonDataProviderCommandLineInfo::IsUninstall()
{
	return m_bUninstall;
}
//////////////////////////////////////////////////////////////////////
DWORD CPresentMonDataProviderCommandLineInfo::GetProcessId()
{
	return m_dwProcessId;
}
//////////////////////////////////////////////////////////////////////
