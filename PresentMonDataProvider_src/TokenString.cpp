// TokenString.cpp: implementation of the CTokenString class.
//
// created by Unwinder
//////////////////////////////////////////////////////////////////////
#include "pch.h"
#include "TokenString.h"
//////////////////////////////////////////////////////////////////////
// Construction/Destruction
//////////////////////////////////////////////////////////////////////
CTokenString::CTokenString()
{
	m_lpBuf			= NULL;
	m_lpTok			= NULL;
	m_lpSep			= NULL;

	m_chSeparator	= 0;
}
//////////////////////////////////////////////////////////////////////
CTokenString::~CTokenString()
{
	DeleteBuf();
}
//////////////////////////////////////////////////////////////////////
LPSTR CTokenString::strtokex(LPCSTR lpString, LPCSTR lpSeparators)
{
	m_chSeparator	= 0;

	if (lpString)
	{
		DeleteBuf();

		m_lpBuf			= new char[strlen(lpString) + 1];
		strcpy_s(m_lpBuf, strlen(lpString) + 1, lpString);

		m_lpTok			= m_lpBuf;
		m_lpSep			= strpbrk(m_lpBuf, lpSeparators);

		if (m_lpSep)
		{
			m_chSeparator	= *m_lpSep;
			*m_lpSep		= 0;
		}

		return m_lpTok;
	}

	if (m_lpSep)
	{
		m_lpTok		= m_lpSep + 1;
		m_lpSep		= strpbrk(m_lpTok, lpSeparators);

		if (m_lpSep)
		{
			m_chSeparator	= *m_lpSep;
			*m_lpSep		= 0;
		}

		return m_lpTok;
	}

	return NULL;
}
//////////////////////////////////////////////////////////////////////
LPSTR CTokenString::strtok(LPCSTR lpString, LPCSTR lpSeparators)
{
	LPSTR lpTok = strtokex(lpString, lpSeparators);

	while (lpTok && !strlen(lpTok))
		lpTok = strtokex(NULL, lpSeparators);

	return lpTok;
}
//////////////////////////////////////////////////////////////////////
void CTokenString::DeleteBuf()
{
	if (m_lpBuf)
		delete [] m_lpBuf;

	m_lpBuf = NULL;
}
//////////////////////////////////////////////////////////////////////
char CTokenString::GetSeparator()
{
	return m_chSeparator;
}
//////////////////////////////////////////////////////////////////////
