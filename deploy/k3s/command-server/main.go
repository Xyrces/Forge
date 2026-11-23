// The toolchain owns only a task checkout and private IPC, never Forge credentials.
package main

import (
	"bufio"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"net"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync"
	"syscall"
	"time"
)

type request struct {
	Shell       string            `json:"shell"`
	Command     string            `json:"command"`
	Cwd         string            `json:"cwd"`
	TimeoutMs   int               `json:"timeoutMs"`
	Environment map[string]string `json:"environment"`
}
type result struct {
	ExitCode int    `json:"exitCode"`
	Stdout   string `json:"stdout"`
	Stderr   string `json:"stderr"`
	TimedOut bool   `json:"timedOut"`
}
type bounded struct {
	sync.Mutex
	b         []byte
	truncated bool
}

func (b *bounded) Write(p []byte) (int, error) {
	b.Lock()
	defer b.Unlock()
	n := len(p)
	left := 65536 - len(b.b)
	if len(p) > left {
		p = p[:left]
		b.truncated = true
	}
	b.b = append(b.b, p...)
	return n, nil
}
func (b *bounded) text() string {
	b.Lock()
	defer b.Unlock()
	s := string(b.b)
	if b.truncated {
		s += "\n[output truncated at 65536 bytes]"
	}
	return s
}
func fail(c net.Conn, e error) {
	_ = json.NewEncoder(c).Encode(result{ExitCode: -1, Stderr: e.Error()})
}
func handle(c net.Conn, root string) {
	defer c.Close()
	_ = c.SetReadDeadline(time.Now().Add(30 * time.Second))
	reader := bufio.NewReader(io.LimitReader(c, 1024*1024+1))
	line, err := reader.ReadBytes('\n')
	if err != nil || len(line) > 1024*1024 {
		fail(c, errors.New("invalid or oversized request"))
		return
	}
	var r request
	if err = json.Unmarshal(line, &r); err != nil {
		fail(c, err)
		return
	}
	_ = c.SetReadDeadline(time.Time{})
	cwd, err := filepath.EvalSymlinks(r.Cwd)
	if err != nil {
		fail(c, err)
		return
	}
	rel, err := filepath.Rel(root, cwd)
	if err != nil || rel == ".." || strings.HasPrefix(rel, "../") || !filepath.IsAbs(cwd) {
		fail(c, errors.New("working directory is outside assigned checkout"))
		return
	}
	if r.TimeoutMs < 1 || r.TimeoutMs > 900000 || strings.TrimSpace(r.Command) == "" {
		fail(c, errors.New("invalid command deadline"))
		return
	}
	shell, shellFlag := "/bin/sh", "-c"
	if r.Shell == "bash" {
		shell, shellFlag = "/bin/bash", "-lc"
	} else if r.Shell != "" && r.Shell != "sh" {
		fail(c, errors.New("unsupported execution shell"))
		return
	}
	cmd := exec.Command(shell, shellFlag, r.Command)
	cmd.Dir = cwd
	cmd.SysProcAttr = &syscall.SysProcAttr{Setpgid: true}
	cmd.WaitDelay = time.Second
	cmd.Env = append(os.Environ(), "GIT_DIR=/run/forge-toolchain-git/repo", "GIT_WORK_TREE="+root, "GIT_CONFIG_NOSYSTEM=1", "GIT_CONFIG_GLOBAL=/dev/null", "GIT_TERMINAL_PROMPT=0")
	for k, v := range r.Environment {
		if strings.HasPrefix(k, "FORGE_SECRET_") && !strings.ContainsAny(k, "=\x00") && !strings.ContainsRune(v, 0) {
			cmd.Env = append(cmd.Env, k+"="+v)
		}
	}
	var stdout, stderr bounded
	cmd.Stdout = &stdout
	cmd.Stderr = &stderr
	if err = cmd.Start(); err != nil {
		fail(c, err)
		return
	}
	done := make(chan error, 1)
	go func() { done <- cmd.Wait() }()
	disconnected := make(chan struct{})
	go func() { var b [1]byte; _, _ = reader.Read(b[:]); close(disconnected) }()
	timer := time.NewTimer(time.Duration(r.TimeoutMs) * time.Millisecond)
	defer timer.Stop()
	timedOut := false
	select {
	case err = <-done:
	case <-disconnected:
		_ = syscall.Kill(-cmd.Process.Pid, syscall.SIGKILL)
		<-done
		return
	case <-timer.C:
		timedOut = true
		_ = syscall.Kill(-cmd.Process.Pid, syscall.SIGKILL)
		err = <-done
	}
	// Shell completion must not leave detached descendants or background jobs running.
	_ = syscall.Kill(-cmd.Process.Pid, syscall.SIGKILL)
	code := 0
	if err != nil {
		code = -1
		var exit *exec.ExitError
		if errors.As(err, &exit) {
			code = exit.ExitCode()
		}
	}
	_ = c.SetWriteDeadline(time.Now().Add(5 * time.Second))
	_ = json.NewEncoder(c).Encode(result{ExitCode: code, Stdout: stdout.text(), Stderr: stderr.text(), TimedOut: timedOut})
}
func main() {
	socket := flag.String("socket", "", "socket path")
	root := flag.String("root", "", "checkout path")
	probe := flag.String("probe", "", "probe socket")
	flag.Parse()
	if *probe != "" {
		c, e := net.DialTimeout("unix", *probe, time.Second)
		if e != nil {
			os.Exit(1)
		}
		_ = c.Close()
		return
	}
	resolved, err := filepath.EvalSymlinks(*root)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	if *socket == "" || !filepath.IsAbs(resolved) {
		os.Exit(1)
	}
	_ = os.MkdirAll("/tmp/home", 0700)
	// A stale socket is pod-local and can only remain after this sidecar restarts.
	if info, e := os.Lstat(*socket); e == nil {
		if info.Mode()&os.ModeSocket == 0 {
			os.Exit(1)
		}
		if e = os.Remove(*socket); e != nil {
			os.Exit(1)
		}
	}
	listener, err := net.Listen("unix", *socket)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	defer listener.Close()
	if err = os.Chmod(*socket, 0600); err != nil {
		os.Exit(1)
	}
	for {
		c, e := listener.Accept()
		if e != nil {
			fmt.Fprintln(os.Stderr, e)
			os.Exit(1)
		}
		go handle(c, resolved)
	}
}
