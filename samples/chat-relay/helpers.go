// Hand-written Go-side adapters that smooth over rough edges in
// the gorilla/websocket API. The three helpers below collapse Go-
// idiomatic call shapes (struct-literal initialization, multi-
// value returns) into the (T, error) and zero-arg-constructor
// shapes that the Overt FFI binds against.
//
// Why these aren't auto-generated: the Overt-side extern fn
// declaration model is per-method-binding, and a Go fn that
// returns three values doesn't fit the (T, error) shape that the
// emitter's Result wrap recognizes. Five lines of Go each is the
// cheapest way to bridge.
//
// When `extern "go" use "<package>"` (the bulk-import work
// scoped in docs/ffi.md §10) lands, these helpers can be replaced
// by the generator's reflection-driven facades. Until then, the
// per-app shim file is the working pattern.

package main

import (
	"net/http"

	"github.com/gorilla/websocket"
)

// NewUpgrader returns the zero-value websocket.Upgrader. The
// Upgrader struct has fields like CheckOrigin and HandshakeTimeout
// that the zero value leaves at sensible defaults for an echo
// server. A real production server would want to set CheckOrigin;
// the chat-relay sample is on localhost so we accept any origin.
func NewUpgrader() websocket.Upgrader {
	return websocket.Upgrader{
		CheckOrigin: func(r *http.Request) bool { return true },
	}
}

// UpgradeConn collapses Upgrader.Upgrade(w, r, nil) into the
// (T, error) shape the FFI's Result wrap expects. The trailing
// `nil` is the response-header argument; Overt has no first-class
// representation for http.Header today, and an echo server doesn't
// need to set custom upgrade-response headers.
func UpgradeConn(u websocket.Upgrader, w http.ResponseWriter, r *http.Request) (*websocket.Conn, error) {
	return u.Upgrade(w, r, nil)
}

// ReadMessageString collapses ReadMessage's
// (messageType int, payload []byte, error) triple into a (string,
// error) pair. We discard the message type because the echo
// server treats every frame as text; a real chat server would
// want to inspect the type for binary, ping, pong, and close
// frames.
func ReadMessageString(c *websocket.Conn) (string, error) {
	_, p, err := c.ReadMessage()
	if err != nil {
		return "", err
	}
	return string(p), nil
}

// WriteMessageString fixes the message type at TextMessage and
// converts the string to []byte. Mirrors ReadMessageString's
// shape on the outbound side.
func WriteMessageString(c *websocket.Conn, msg string) error {
	return c.WriteMessage(websocket.TextMessage, []byte(msg))
}

// ListenAndServeNoHandler fixes http.ListenAndServe's second
// argument at nil (use the default mux). Overt has no first-class
// representation for http.Handler today; the FFI binding's
// signature is cleaner with the handler-less shape.
func ListenAndServeNoHandler(addr string) error {
	return http.ListenAndServe(addr, nil)
}
