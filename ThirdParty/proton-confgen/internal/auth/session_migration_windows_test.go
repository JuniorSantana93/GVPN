//go:build windows

package auth

import (
	"bytes"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"

	"protonvpn-wg-confgen/internal/api"

	"golang.org/x/sys/windows"
)

func writeLegacySession(t *testing.T, file string) []byte {
	t.Helper()
	legacy, err := json.Marshal(SavedSession{
		Session:   &api.Session{AccessToken: "synthetic-access", RefreshToken: "synthetic-refresh", UID: "synthetic-uid", ExpiresIn: 3600},
		Username:  "synthetic@example.com",
		SavedAt:   time.Now().Add(-time.Minute),
		ExpiresAt: time.Now().Add(time.Hour),
	})
	if err != nil {
		t.Fatalf("marshal legacy session: %v", err)
	}
	if err := os.WriteFile(file, legacy, 0o600); err != nil {
		t.Fatalf("write legacy session: %v", err)
	}
	return legacy
}

func assertEncryptedSession(t *testing.T, file string) {
	t.Helper()
	payload, err := os.ReadFile(file)
	if err != nil {
		t.Fatalf("read migrated session: %v", err)
	}
	if !bytes.HasPrefix(payload, []byte(encryptedSessionHeader)) {
		t.Fatalf("migrated session is not DPAPI protected: %q", payload)
	}
	entries, err := os.ReadDir(filepath.Dir(file))
	if err != nil {
		t.Fatalf("read session directory: %v", err)
	}
	for _, entry := range entries {
		if strings.HasPrefix(entry.Name(), ".protonvpn-session-") && strings.HasSuffix(entry.Name(), ".tmp") {
			t.Fatalf("migration left temporary file %q", entry.Name())
		}
	}
}

func holdWithoutDeleteShare(t *testing.T, file string) windows.Handle {
	t.Helper()
	name, err := windows.UTF16PtrFromString(file)
	if err != nil {
		t.Fatalf("UTF16PtrFromString: %v", err)
	}
	handle, err := windows.CreateFile(name, windows.GENERIC_READ, windows.FILE_SHARE_READ|windows.FILE_SHARE_WRITE, nil, windows.OPEN_EXISTING, windows.FILE_ATTRIBUTE_NORMAL, 0)
	if err != nil {
		t.Fatalf("CreateFile without FILE_SHARE_DELETE: %v", err)
	}
	return handle
}

func TestWindowsLegacySessionMigrationClearsReadonlyAndPreservesIdentity(t *testing.T) {
	file := filepath.Join(t.TempDir(), "proton-session.json")
	writeLegacySession(t, file)
	name, err := windows.UTF16PtrFromString(file)
	if err != nil {
		t.Fatalf("UTF16PtrFromString: %v", err)
	}
	if err := windows.SetFileAttributes(name, windows.FILE_ATTRIBUTE_READONLY); err != nil {
		t.Fatalf("SetFileAttributes(readonly): %v", err)
	}

	store := NewSessionStore(file)
	session, _, err := store.Load("synthetic@example.com")
	if err != nil {
		t.Fatalf("Load() migration error = %v", err)
	}
	if session == nil || session.AccessToken != "synthetic-access" {
		t.Fatalf("Load() = %#v, want synthetic session", session)
	}
	assertEncryptedSession(t, file)
	if username, err := store.Username(); err != nil || username != "synthetic@example.com" {
		t.Fatalf("Username() = %q, %v", username, err)
	}
}

func TestWindowsLegacySessionMigrationRetriesTransientSharingViolation(t *testing.T) {
	file := filepath.Join(t.TempDir(), "proton-session.json")
	writeLegacySession(t, file)
	handle := holdWithoutDeleteShare(t, file)
	result := make(chan error, 1)
	go func() {
		_, _, err := NewSessionStore(file).Load("synthetic@example.com")
		result <- err
	}()
	time.Sleep(sessionReplaceRetryDelay + 10*time.Millisecond)
	if err := windows.CloseHandle(handle); err != nil {
		t.Fatalf("CloseHandle: %v", err)
	}
	if err := <-result; err != nil {
		t.Fatalf("Load() after transient sharing violation = %v", err)
	}
	assertEncryptedSession(t, file)
}

func TestWindowsLegacySessionMigrationPreservesCacheAfterPersistentSharingViolation(t *testing.T) {
	file := filepath.Join(t.TempDir(), "proton-session.json")
	legacy := writeLegacySession(t, file)
	handle := holdWithoutDeleteShare(t, file)
	_, _, err := NewSessionStore(file).Load("synthetic@example.com")
	if closeErr := windows.CloseHandle(handle); closeErr != nil {
		t.Fatalf("CloseHandle: %v", closeErr)
	}
	if !IsSessionPersistenceError(err) {
		t.Fatalf("Load() error = %T %v, want SessionPersistenceError", err, err)
	}
	current, readErr := os.ReadFile(file)
	if readErr != nil {
		t.Fatalf("read preserved legacy session: %v", readErr)
	}
	if !bytes.Equal(current, legacy) {
		t.Fatal("persistent replace failure changed the previous session")
	}
}

func TestWindowsLegacySessionMigrationStressSerializesStores(t *testing.T) {
	file := filepath.Join(t.TempDir(), "proton-session.json")
	writeLegacySession(t, file)
	const workers = 48
	start := make(chan struct{})
	errs := make(chan error, workers)
	var group sync.WaitGroup
	for range workers {
		group.Add(1)
		go func() {
			defer group.Done()
			<-start
			session, _, err := NewSessionStore(file).Load("synthetic@example.com")
			if err != nil {
				errs <- err
				return
			}
			if session == nil || session.AccessToken != "synthetic-access" {
				errs <- errors.New("synthetic session was not preserved")
			}
		}()
	}
	close(start)
	group.Wait()
	close(errs)
	for err := range errs {
		t.Fatalf("concurrent migration failed: %v", err)
	}
	assertEncryptedSession(t, file)
}
