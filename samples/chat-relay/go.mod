module chat-relay

go 1.21

require (
	github.com/gorilla/websocket v1.5.1
	overt-runtime v0.0.0
)

require golang.org/x/net v0.17.0 // indirect

// In-repo runtime: replace the published path with the sibling
// directory under runtime/go. Removes the need to publish the
// runtime as a real Go module while it's still iterating.
replace overt-runtime => ../../runtime/go
