// PresentMonDataProviderConnectWnd.h : header file
//
// created by Unwinder
/////////////////////////////////////////////////////////////////////////////
#ifndef _PRESENTMONDATAPROVIDERCONNECTWND_H_INCLUDED_
#define _PRESENTMONDATAPROVIDERCONNECTWND_H_INCLUDED_
/////////////////////////////////////////////////////////////////////////////
#if _MSC_VER > 1000
#pragma once
#endif // _MSC_VER > 1000
/////////////////////////////////////////////////////////////////////////////
#define CONNECT_WND_NAME	"PresentMonDataProviderConnectWnd"
/////////////////////////////////////////////////////////////////////////////
#define CHAR_BUF_SIZE 4096
/////////////////////////////////////////////////////////////////////////////
const UINT UM_SET_TARGET_PROCESS = ::RegisterWindowMessage("UM_SET_TARGET_PROCESS");
/////////////////////////////////////////////////////////////////////////////
#include "PresentMonDataProviderThread.h"
#include "PresentMonDataBuffer.h"
/////////////////////////////////////////////////////////////////////////////
class CPresentMonDataProviderConnectWnd : public CWnd
{
// Construction
public:
	CPresentMonDataProviderConnectWnd();
	virtual ~CPresentMonDataProviderConnectWnd();

// Attributes
public:

// Operations
public:

// Overrides
	// ClassWizard generated virtual function overrides
	//{{AFX_VIRTUAL(CPresentMonDataProviderConnectWnd)
	protected:
	virtual LRESULT DefWindowProc(UINT message, WPARAM wParam, LPARAM lParam);
	//}}AFX_VIRTUAL

// Implementation
public:
	void CreateThread();
	void DestroyThread();
	void SetTargetProcessId(DWORD dwProcessId);

	CString GetCfgPath();

	CString GetConfigStr(LPCSTR lpSection, LPCSTR lpName, LPCTSTR lpDefault);
	void	SetConfigStr(LPCSTR lpSection, LPCSTR lpName, LPCSTR lpValue);
	int		GetConfigInt(LPCSTR lpSection, LPCSTR lpName, int nDefault);
	void	SetConfigInt(LPCSTR lpSection, LPCSTR lpName, int nValue);
	DWORD	GetConfigHex(LPCSTR lpSection, LPCSTR lpName, DWORD dwDefault);
	void	SetConfigHex(LPCSTR lpSection, LPCSTR lpName, DWORD dwValue);

protected:
	CPresentMonDataProviderThread*	m_lpThread;
	CPresentMonDataBuffer			m_buffer;
	DWORD							m_dwTargetProcessId;

	// Generated message map functions
protected:
	//{{AFX_MSG(CPresentMonDataProviderConnectWnd)
		// NOTE - the ClassWizard will add and remove member functions here.
	//}}AFX_MSG
	DECLARE_MESSAGE_MAP()
public:
	afx_msg int OnCreate(LPCREATESTRUCT lpCreateStruct);
	afx_msg void OnDestroy();
};
/////////////////////////////////////////////////////////////////////////////
//{{AFX_INSERT_LOCATION}}
// Microsoft Visual C++ will insert additional declarations immediately before the previous line.
/////////////////////////////////////////////////////////////////////////////
#endif
/////////////////////////////////////////////////////////////////////////////
