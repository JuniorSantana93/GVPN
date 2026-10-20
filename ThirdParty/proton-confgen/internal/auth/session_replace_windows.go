//go:build windows

package auth

import (
	"errors"
	"time"

	"golang.org/x/sys/windows"
)

const (
	sessionReplaceAttempts   = 5
	sessionReplaceRetryDelay = 50 * time.Millisecond
)

func replaceSessionFile(source, target string) error {
	sourcePtr, err := windows.UTF16PtrFromString(source)
	if err != nil {
		return err
	}
	targetPtr, err := windows.UTF16PtrFromString(target)
	if err != nil {
		return err
	}
	if err := clearSessionReadOnlyAttribute(targetPtr); err != nil {
		return err
	}
	for attempt := range sessionReplaceAttempts {
		err = windows.MoveFileEx(sourcePtr, targetPtr, windows.MOVEFILE_REPLACE_EXISTING|windows.MOVEFILE_WRITE_THROUGH)
		if err == nil {
			return nil
		}
		if !isRetryableSessionReplaceError(err) || attempt == sessionReplaceAttempts-1 {
			return err
		}
		time.Sleep(sessionReplaceRetryDelay)
	}
	return err
}

func clearSessionReadOnlyAttribute(target *uint16) error {
	attributes, err := windows.GetFileAttributes(target)
	if errors.Is(err, windows.ERROR_FILE_NOT_FOUND) {
		return nil
	}
	if err != nil {
		return err
	}
	if attributes&windows.FILE_ATTRIBUTE_READONLY == 0 {
		return nil
	}
	return windows.SetFileAttributes(target, attributes&^windows.FILE_ATTRIBUTE_READONLY)
}

func isRetryableSessionReplaceError(err error) bool {
	return errors.Is(err, windows.ERROR_ACCESS_DENIED) || errors.Is(err, windows.ERROR_SHARING_VIOLATION)
}
