// PresentMonDataProviderCommandLineInfo.h: interface for the CPresentMonDataProviderCommandLineInfo class.
//
// created by Unwinder
//////////////////////////////////////////////////////////////////////
#ifndef _PRESENTMONDATAPROVIDERCOMMANDLINEINFO_H_INCLUDED_
#define _PRESENTMONDATAPROVIDERCOMMANDLINEINFO_H_INCLUDED_
//////////////////////////////////////////////////////////////////////
#if _MSC_VER > 1000
#pragma once
#endif // _MSC_VER > 1000
//////////////////////////////////////////////////////////////////////
class CPresentMonDataProviderCommandLineInfo : public CCommandLineInfo  
{
public:
	BOOL	IsInstall();
	BOOL	IsUninstall();
	DWORD	GetProcessId();

	virtual void ParseParam(LPCTSTR lpszParam,BOOL bFlag,BOOL bLast );

	CPresentMonDataProviderCommandLineInfo();
	virtual ~CPresentMonDataProviderCommandLineInfo();

private: 
	BOOL		m_bInstall;
	BOOL		m_bUninstall;
	DWORD		m_dwProcessId;
};
//////////////////////////////////////////////////////////////////////
#endif 
//////////////////////////////////////////////////////////////////////
