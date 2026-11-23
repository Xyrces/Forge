package main

import (
	"encoding/json"
	"fmt"
	"net"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func exchange(t *testing.T, root string, r request) result {
	t.Helper()
	client, server := net.Pipe()
	defer client.Close()
	go handle(server, root)
	_ = client.SetDeadline(time.Now().Add(5 * time.Second))
	if err := json.NewEncoder(client).Encode(r); err != nil {
		t.Fatal(err)
	}
	var result result
	if err := json.NewDecoder(client).Decode(&result); err != nil {
		t.Fatal(err)
	}
	return result
}
func TestExitOutputAndProjectEnvironment(t *testing.T) {
	root := t.TempDir()
	r := exchange(t, root, request{Command: "printf '%s' \"$FORGE_SECRET_TEST\"; printf err >&2; exit 7", Cwd: root, TimeoutMs: 1000, Environment: map[string]string{"FORGE_SECRET_TEST": "allowed"}})
	if r.ExitCode != 7 || r.Stdout != "allowed" || r.Stderr != "err" {
		t.Fatalf("%+v", r)
	}
}
func TestCwdEscapesRejected(t *testing.T) {
	root := t.TempDir()
	outside := t.TempDir()
	if err := os.Symlink(outside, filepath.Join(root, "escape")); err != nil {
		t.Fatal(err)
	}
	for _, cwd := range []string{outside, filepath.Join(root, "escape")} {
		r := exchange(t, root, request{Command: "pwd", Cwd: cwd, TimeoutMs: 1000})
		if r.ExitCode != -1 || !strings.Contains(r.Stderr, "outside assigned") {
			t.Fatalf("%+v", r)
		}
	}
}
func TestTimeoutAndDisconnectKillDescendants(t *testing.T) {
	root := t.TempDir()
	marker := filepath.Join(root, "unexpected")
	command := fmt.Sprintf("(sleep 1; touch '%s') & wait", marker)
	r := exchange(t, root, request{Command: command, Cwd: root, TimeoutMs: 50})
	if !r.TimedOut {
		t.Fatalf("%+v", r)
	}
	client, server := net.Pipe()
	go handle(server, root)
	if err := json.NewEncoder(client).Encode(request{Command: command, Cwd: root, TimeoutMs: 4000}); err != nil {
		t.Fatal(err)
	}
	time.Sleep(50 * time.Millisecond)
	_ = client.Close()
	time.Sleep(1100 * time.Millisecond)
	if _, err := os.Stat(marker); !os.IsNotExist(err) {
		t.Fatal("descendant survived cancellation")
	}
}
func TestOutputBound(t *testing.T) {
	root := t.TempDir()
	r := exchange(t, root, request{Command: "head -c 200000 /dev/zero", Cwd: root, TimeoutMs: 1000})
	if len(r.Stdout) > 65600 || !strings.Contains(r.Stdout, "truncated") {
		t.Fatal("output not bounded")
	}
}

func TestVerificationPreservesBashSemantics(t *testing.T) {
	root := t.TempDir()
	r := exchange(t, root, request{Shell: "bash", Command: "set -o pipefail; values=(one two); printf '%s' \"${values[1]}\"", Cwd: root, TimeoutMs: 1000})
	if r.ExitCode != 0 || r.Stdout != "two" {
		t.Fatalf("%+v", r)
	}
}
