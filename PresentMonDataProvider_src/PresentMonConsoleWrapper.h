/////////////////////////////////////////////////////////////////////////////
#pragma once
/////////////////////////////////////////////////////////////////////////////
#include "PresentMonAPI.h"
#include "PMDPSharedMemory.h"
/////////////////////////////////////////////////////////////////////////////
class CPresentMonConsoleWrapper
{
public:
	CPresentMonConsoleWrapper(LPCSTR lpPath, LPCSTR lpCmd);
	virtual ~CPresentMonConsoleWrapper();

	DWORD	GetVersionMajor();
	DWORD	GetVersionMinor();
	DWORD	GetVersionPatch();
	BOOL	IsVersionGreaterOrEqual(DWORD dwMajor, DWORD dwMinor, DWORD dwPatch);

	BOOL	Create(DWORD dwProcessId);
	void	Destroy();
	void	Watchdog();

	void	InitColumnsMap(LPCSTR lpLine);
	DWORD	GetMappedColumn(DWORD dwIndex);

	DWORD	ConsumeFrames(PMDP_FRAME_DATA* lpFrames, DWORD dwMaxFrames);
	DWORD	ParseFramesBatch(LPBYTE lpBuffer, DWORD dwSize, PMDP_FRAME_DATA* lpFrames, DWORD dwMaxFrames);
	BOOL	ParseFrame(LPCSTR lpLine, PMDP_FRAME_DATA* lpFrame);

	uint32_t EncodeRuntime(LPCSTR lpString);
	uint32_t EncodePresentMode(LPCSTR lpString);

protected:
	CString			m_strPath;
	CString			m_strCmd;
	DWORD			m_dwVersionMajor;
	DWORD			m_dwVersionMinor;
	DWORD			m_dwVersionPatch;
	DWORD			m_dwProcessId;
	DWORD			m_dwTimestamp;

	HANDLE			m_hProcess;
	HANDLE			m_hThread;
	HANDLE			m_hStdOutRead;
	HANDLE			m_hStdOutWrite;

	CString			m_strLine;

	CDWordArray		m_columns;

	BOOL			m_bTrackInput;
	BOOL			m_bV1_Metrics;
	BOOL			m_bV2_Metrics;
};
/////////////////////////////////////////////////////////////////////////////

