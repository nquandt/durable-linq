# DurableLinq.Analyzers

Roslyn analyzer for `async Durable<T>` methods. `DL0001` flags a `catch` that can swallow a failed step;
`DL0002` flags an `await` on something that isn't a durable. Compile-time only; never a runtime
dependency.

Full documentation is at <https://nquandt.github.io/durable-linq/analyzers/>.
