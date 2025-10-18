#include "pch.h"
#include "PresentMonDataBuffer.h"
/////////////////////////////////////////////////////////////////////////////
CPresentMonDataBuffer::CPresentMonDataBuffer()
{
	m_hMapFile = NULL;
	m_pMapAddr = NULL;
}
/////////////////////////////////////////////////////////////////////////////
CPresentMonDataBuffer::~CPresentMonDataBuffer()
{
	DestroySharedMemory();
}
/////////////////////////////////////////////////////////////////////////////
void CPresentMonDataBuffer::CreateSharedMemory()
{
	m_hMapFile = CreateFileMapping(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, 0, sizeof(PMDP_SHARED_MEMORY), "PMDPSharedMemory");

	if (m_hMapFile)
	{
		m_pMapAddr = (LPPMDP_SHARED_MEMORY)MapViewOfFile(m_hMapFile, FILE_MAP_ALL_ACCESS, 0, 0, 0);

		if (m_pMapAddr)
		{
			memset(m_pMapAddr, 0, sizeof(PMDP_SHARED_MEMORY));

			m_pMapAddr->dwSignature			= 'PMDP';
			m_pMapAddr->dwVersion			= 0x00020000;

			m_pMapAddr->dwFrameArrEntrySize = sizeof(PMDP_FRAME_DATA);
			m_pMapAddr->dwFrameArrOffset	= (DWORD)((LPBYTE)m_pMapAddr->arrFrame - (LPBYTE)m_pMapAddr);
			m_pMapAddr->dwFrameArrSize		= _countof(m_pMapAddr->arrFrame);

			m_pMapAddr->dwFrameCount		= 0;
			m_pMapAddr->dwFramePos			= 0;

			m_pMapAddr->dwStatus			= PMDP_STATUS_OK;
		}
	}
}
/////////////////////////////////////////////////////////////////////////////
void CPresentMonDataBuffer::DestroySharedMemory()
{
	if (m_pMapAddr)
	{
		__try
		{
			m_pMapAddr->dwSignature = 0xDEAD;

			UnmapViewOfFile(m_pMapAddr);
		}
		__except (EXCEPTION_EXECUTE_HANDLER)
		{
		}
	}

	m_pMapAddr = NULL;

	if (m_hMapFile)
		CloseHandle(m_hMapFile);

	m_hMapFile = NULL;
}
/////////////////////////////////////////////////////////////////////////////
void CPresentMonDataBuffer::SetFrameData(uint32_t dwFrames, PMDP_FRAME_DATA* lpFrameData)
{
	if (dwFrames)
	{
		if (m_pMapAddr)
		{
			for (DWORD dwFrame = 0; dwFrame < dwFrames; dwFrame++)
			{
				CopyMemory(m_pMapAddr->arrFrame + m_pMapAddr->dwFramePos, lpFrameData + dwFrame, sizeof(PMDP_FRAME_DATA));

				m_pMapAddr->dwFrameCount++;
				m_pMapAddr->dwFramePos = (m_pMapAddr->dwFramePos + 1) & (_countof(m_pMapAddr->arrFrame) - 1);
			}
		}
	}
}
/////////////////////////////////////////////////////////////////////////////
void CPresentMonDataBuffer::ResetFrameData()
{
	if (m_pMapAddr)
	{
		m_pMapAddr->dwFrameCount	= 0;
		m_pMapAddr->dwFramePos		= 0;

	}
}
/////////////////////////////////////////////////////////////////////////////
void CPresentMonDataBuffer::OnInitialize(PM_STATUS err)
{
	if (m_pMapAddr)
	{
		if (err != PM_STATUS_SUCCESS)
			m_pMapAddr->dwStatus = PMDP_STATUS_INIT_FAILED;
	}
}
/////////////////////////////////////////////////////////////////////////////
void CPresentMonDataBuffer::OnStartStream(PM_STATUS err)
{
	if (m_pMapAddr)
	{
		if (err != PM_STATUS_SUCCESS)
			m_pMapAddr->dwStatus = PMDP_STATUS_START_STREAM_FAILED;
	}
}
/////////////////////////////////////////////////////////////////////////////
void CPresentMonDataBuffer::OnGetFrameData(PM_STATUS err)
{
	if (m_pMapAddr)
	{
		if ((err != PM_STATUS_SUCCESS)/* &&
			(err != PM_STATUS_NO_DATA)*/)
			m_pMapAddr->dwStatus = PMDP_STATUS_GET_FRAME_DATA_FAILED;
		else
			m_pMapAddr->dwStatus = PMDP_STATUS_OK;
	}
}
/////////////////////////////////////////////////////////////////////////////
void CPresentMonDataBuffer::OnStopStream(PM_STATUS err)
{
	ResetFrameData();
}
/////////////////////////////////////////////////////////////////////////////
void CPresentMonDataBuffer::SetStatus(DWORD dwStatus)
{
	if (m_pMapAddr)
		m_pMapAddr->dwStatus = dwStatus;
}
/////////////////////////////////////////////////////////////////////////////
