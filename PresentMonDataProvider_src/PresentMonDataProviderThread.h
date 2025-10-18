#ifndef _PRESENTMONDATAPROVIDERTHREAD_H_
#define _PRESENTMONDATAPROVIDERTHREAD_H_
/////////////////////////////////////////////////////////////////////////////
#if _MSC_VER > 1000
#pragma once
#endif // _MSC_VER > 1000
/////////////////////////////////////////////////////////////////////////////
#include "PresentMonDataBuffer.h"
#include "PresentMonConsoleWrapper.h"
#include "PresentMonAPI.h"
/////////////////////////////////////////////////////////////////////////////
// CPresentMonDataProviderThread thread
/////////////////////////////////////////////////////////////////////////////
class CHAL;
class CPresentMonDataProviderThread : public CWinThread
{
	DECLARE_DYNCREATE(CPresentMonDataProviderThread)
protected:
	CPresentMonDataProviderThread();           // protected constructor used by dynamic creation

// Attributes
public:
	CPresentMonDataProviderThread(CPresentMonDataBuffer* lpDataBuffer);

// Operations
public:
	void	Kill();
	void	Destroy();
	void	SetTargetProcessId(DWORD dwProcessId);

// Overrides
	// ClassWizard generated virtual function overrides
	//{{AFX_VIRTUAL(CPresentMonDataProviderThread)
	public:
	virtual BOOL InitInstance();
	virtual int	ExitInstance();
	//}}AFX_VIRTUAL

// Implementation
protected:
	CPresentMonDataBuffer*		m_lpDataBuffer;
	HANDLE						m_hEventKill;

	DWORD						m_dwActiveProcessId;
	DWORD						m_dwTargetProcessId;
	DWORD						m_dwForegroundProcessId;
	DWORD						m_dwForegroundProcessTimestamp;

	BOOL						ConsumerLoop();
	void						StartStreaming(PM_SESSION_HANDLE session, DWORD dwTargetProcessId);
	void						StopStreaming(PM_SESSION_HANDLE session);

	BYTE						GetQueryElementAsByte(PM_QUERY_ELEMENT* lpElements, DWORD dwIndex, LPBYTE lpBlob, BYTE defaultValue);
	uint32_t					GetQueryElementAsUint32(PM_QUERY_ELEMENT* lpElements, DWORD dwIndex, LPBYTE lpBlob, uint32_t defaultValue);
	uint64_t					GetQueryElementAsUint64(PM_QUERY_ELEMENT* lpElements, DWORD dwIndex, LPBYTE lpBlob, uint64_t defaultValue);
	double						GetQueryElementAsDouble(PM_QUERY_ELEMENT* lpElements, DWORD dwIndex, LPBYTE lpBlob, double defaultValue);

	BOOL						ConsoleConsumerLoop();
	void						StartConsoleStreaming(DWORD dwTargetProcessId, CPresentMonConsoleWrapper* lpConsoleWrapper);
	void						StopConsoleStreaming(CPresentMonConsoleWrapper* lpConsoleWrapper);

	DWORD						GetForegroundProcessId();
	DWORD						GetForegroundProcessIdDelayed();

	BOOL						IsFrameWindow(HWND hFrameWnd);
	HWND						GetCoreWindow(HWND hFrameWnd);

	DWORD						GetFrameDelay(PMDP_FRAME_DATA* lpFrame);

	virtual ~CPresentMonDataProviderThread();

	// Generated message map functions
	//{{AFX_MSG(CPresentMonDataProviderThread)
		// NOTE - the ClassWizard will add and remove member functions here.
	//}}AFX_MSG

	DECLARE_MESSAGE_MAP()
};
/////////////////////////////////////////////////////////////////////////////
//{{AFX_INSERT_LOCATION}}
// Microsoft Visual C++ will insert additional declarations immediately before the previous line.
/////////////////////////////////////////////////////////////////////////////
#endif 
/////////////////////////////////////////////////////////////////////////////
