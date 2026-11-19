//go:build !windows

package winapi

// WhoLocks: files are not locked against deletion outside Windows.
func WhoLocks(paths []string) ([]LockHolder, error) { return nil, nil }

// HoldFile: files cannot be locked against deletion outside Windows.
func HoldFile(path string) (func(), error) { return func() {}, nil }
