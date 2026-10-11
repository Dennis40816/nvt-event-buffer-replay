# UI event failure observation

The Avalonia host uses the `Nvt.Core` 0.9.0 package's `UiEventRunner`.
The 22 formerly `async void` MainWindow handlers now synchronously start
observed Task operations. Work still begins on the calling UI context.
The U1 Load and Decode command delegates also use that runner, including
bound buttons and the Load shortcut. Nested observation reports a failure
once: an inner observed failure completes before an outer runner sees it.
The startup capture subscription follows the same boundary.

`UiFailureReporter` maps stable operation identifiers to failure labels and
uses the existing `SetBusy(false, status)` / `SessionStatusText` path. Load
failures retain `Load failed · <message>`. Existing domain catches, cancellation
messages, success messages and cleanup remain inside their Task methods.
Unexpected preview failures stop preview playback; unexpected setup failures
refresh setup validation so the Decode button reflects whether retry is valid.
These cleanups run only for their own operation names.
Reporter failures use a nonthrowing diagnostics fallback that writes only to
Trace. The UI runner does not change whether an operation succeeded.

Handlers pass `CancellationToken.None` at the entry boundary because their
operations create and own their cancellation sources. Existing operation
cancellation catches remain in those operations. When a caller supplies a
cancelled token to the runner, cancellation is silent only for an exception
carrying that same token; other cancellation is a reported failure.

The injected System TimeProvider preserves the zoom hint, output preview
schedule and review-save UTC timestamp while making those extracted operations
use the host's time seam.

`App.Initialize` registers `Dispatcher.UIThread.UnhandledException` and
`TaskScheduler.UnobservedTaskException`. Both route to the window's reporter
when a desktop window is available. Unobserved task reporting posts to the UI
thread before accessing the window. Before window initialization, or if the
UI dispatcher is stopped, the same adapter writes diagnostics only.
The hooks do not set `Handled` or call `SetObserved`: reporting preserves the
runtime's unhandled-exception policy, including startup crashes. The desktop
lifetime removes the hooks on exit.

Headless tests cover failure observation, cancellation identity, fallback
isolation and the existing bound command entries. Shared Avalonia-session
tests check actual status and busy controls. Architecture tests include a
non-event async-void mutation rejected by the health ratchet and a Task
migration that lowers its baseline. Rendering and existing test async-lambda
candidates remain outside this change.
