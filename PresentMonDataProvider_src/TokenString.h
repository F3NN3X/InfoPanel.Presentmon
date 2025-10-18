// TokenString.h: interface for the CTokenString class.
//
// created by Unwinder
//////////////////////////////////////////////////////////////////////
#ifndef _TOKENSTRING_H_INCLUDED_
#define _TOKENSTRING_H_INCLUDED_
//////////////////////////////////////////////////////////////////////
#if _MSC_VER > 1000
#pragma once
#endif // _MSC_VER > 1000
//////////////////////////////////////////////////////////////////////
class CTokenString  
{
public:
	LPSTR strtok(LPCSTR lpString, LPCSTR lpSeparators);
		//reenterable implementation of CRT strtok

	LPSTR strtokex(LPCSTR lpString, LPCSTR lpSeparators);
		//similar to strtok, but returns empty strings for separators

		//if an empty string is returned, separator can be requested
		//via GetSeparator

	char GetSeparator();
		//returns the last separator

	CTokenString();
	virtual ~CTokenString();

private:

	char	m_chSeparator;
	void	DeleteBuf();

	LPSTR	m_lpBuf;
	LPSTR	m_lpSep;
	LPSTR	m_lpTok;
};
//////////////////////////////////////////////////////////////////////
#endif
//////////////////////////////////////////////////////////////////////
