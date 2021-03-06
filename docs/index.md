# PooledAwait

Low-allocation utilies for writing `async` methods, and related tools

### Contents

- [`PooledValueTask` / `PooledValueTask<T>`](#pooledvaluetask--pooledvaluetaskt)
- [`PooledTask` / `PooledTask<T>`](#pooledtask--pooledtaskt)
- [`FireAndForget`](#fireandforget)
- [`ConfiguredYieldAwaitable`](#configuredyieldawaitable)
- [`ValueTaskCompletionSource<T>`](#valuetaskcompletionsourcet)
- [`PooledValueTaskSource / PooledValueTaskSource<T>`](#pooledvaluetasksource--pooledvaluetasksourcet)
- [`LazyTaskCompletionSource / LazyTaskCompletionSource<T>`](#lazytaskcompletionsource--lazytaskcompletionsourcet)
- [`Pool`](#pool)

---

## `PooledValueTask` / `PooledValueTask<T>`

These are the main tools of the library; their purpose is to remove the boxing of the async state-machine and builder that happens when a method
marked `async` performs an `await` on an awaitable target that *is not yet complete*, i.e.

``` c#
async ValueTask<int> SomeMethod()
{
	await Task.Yield(); // *is not yet complete*
	return 42
}
```

If you've ever looked at an application that uses `async` / `await` in a memory profiler and seen things like `System.Runtime.CompilerServices.AsyncTaskMethodBuilder``1.AsyncStateMachineBox``1`
or `YourLib.<<SomeMethod>g__Inner|8_0>d`, then that's what I'm talking about. You can avoid this by simply using a different return type:

- `PooledValueTask<T>` instead of `ValueTask<T>`
- `PooledValueTask` instead of `ValueTask`

For `private` / `internal` methods, you can probably just *change the return type directly*:

``` c#
private async PooledValueTask<int> SomeMethod()
{
	await Task.Yield(); // *is not yet complete*
	return 42
}
```

For methods on your `public` API surface, you can use a "local function" to achieve the same thing without changing the exposed return type:

``` c#
public ValueTask<int> SomeMethod() // not marked async
{
	return Impl();
	async PooledValueTask<int>() Impl()
	{
		await Task.Yield(); // *is not yet complete*
		return 42
	}
}
```

(all of the `Pooled*` types have `implicit` conversion operators to their more well-recognized brethren).

And that's it! That's all you have to do. The "catch" (there's always a catch) is that awaiting the same pending operation *more than once* **no longer works**:

``` c#
var pending = SomeIncompleteMethodAsync(); // note no "await" here

var x = await pending;
var y = await pending; // BOOM! await the **same result**
```

In reality, **this almost never happens**. Usually you `await` something *once*, *almost always* right away. So... yeah.

---

## `PooledTask` / `PooledTask<T>`

These work very similarly to `PooledValueTask[<T>]`, but for the `Task[<T>]` API. It can't be *quite* as frugal, as in most cases a `Task[<T>]`
will still need to be allocated (unless it is the non-generic `PooledTask` signature, and the operation completes synchronously), but it
still avoids the state-machine box etc. Note that this API **is not** impacted by the "you can only await it once" change (you can
await these as many times as you like - they are, after all, `Task[<T>]`), but again: *this is used incredibly rarely anyway*.

## `FireAndForget`

Ever find yourself needing a fire-and-forget API? This adds one. All you do is declare the return type as `FireAndForget`:

``` c#
FireAndForget SomeMethod(...) {
   // .. things before the first incomplete await happen on the calling thread
   await SomeIncompleteMethod();
   // .. other bits continue running in the background
}
```

As soon as the method uses `await` against an incomplete operation, the calling
task regains control as though it were complete; the rest of the operation continues in the background. The caller can simply `await`
the fire-and-forget method with confidence that it only runs synchronously to the first incomplete operation. If you're not in an `async`
method, you can use "discard" to tell the compiler not to tell you to `await` it:

``` c#
_ = SomeFireAndForgetMethodAsync();
```

You won't get unobserved-task-exception problems. If you want to see any exceptions that happen, there is an event `FireAndForget.Exception`
that you can subscribe to. Otherwise, they just evaporate.

---

## `ConfiguredYieldAwaitable`

Related to `FireAndForget` - when you `await Task.Yield()` it always respects the sync-context/task-scheduler; sometimes *you don't want to*.
For many awaitables there is a `.ConfigureAwait(continueOnCapturedContext: false)` method that you can use to suppress this, but
not on `Task.Yield()`... *until now*. Usage is, as you would expect:

``` c#
await Task.Yield().ConfigureAwait(false);
```

---

## `ValueTaskCompletionSource<T>`

Do you make use of `TaskCompletionSource<T>`? Do you hate that this adds another allocation *on top of* the `Task<T>` that you actually wanted?
`ValueTaskCompletionSource<T>` is your friend. It uses smoke and magic to work like `TaskCompletionSource<T>`, but without the extra
allocation (unless it discovers that the magic isn't working for your system). Usage:

``` c#
var source = ValueTaskCompletionSource<int>.Create();
// ...
source.TrySetResult(42); // etc
```

The main difference here is that you now have a `struct` instead of a `class`. If you want to test whether an instance is a *real* value
(as opposed to the `default`), check `.HasTask`.

---

## `PooledValueTaskSource` / `PooledValueTaskSource<T>`

These again work like `TaskCompletionSource<T>`, but a: for `ValueType[<T>]`, and b: with the same zero-allocation features that
`PooledValueTask` / `PooledValueTask<T>` exhibit. Once again, the "catch" is that you can only await their `.Task` *once*. Usage:

``` c#
var source = PooledValueTaskSource<int>.Create();
// ...
source.TrySetResult(42); // etc
```

---

## `LazyTaskCompletionSource / LazyTaskCompletionSource<T>`

Sometimes, you have an API where you *aren't sure* whether someone is subscribing to the `Task`/`Task<T>` results - for example
you have properties like:

``` c#
public Task SomeStepCompleted { get; }
```

It would be a shame to allocate a `Task` for this *just in case*, so `LazyTaskCompletionSource[<T>]` allows you to *rent* state
that can manage *lazily* creating a task. If the `.Task` is read before the value is set, a *source* is used to provide a
pending task; if the result gets set before the value is read, then some optimizations may be possible (`Task.CompletedTask`, etc).
And if the `.Task` is never queried: no task or source is allocated. These types are disposable; disposing them releases any
rented state for re-use.

---

## `Pool`

Ever need a light-weight basic pool of objects? That's this. Nothing fancy. The first API is a simple get/put:

``` c#
var obj = Pool.TryRent<SomeType>() ?? new SomeType();
// ...
Pool.Return(obj);
```

Note that it leaves creation to you (hence the `?? new SomeType()`), and it is the caller's responsibility to not retain and access
a reference object that you have notionally returned to the pool.

Considerations:

- you may wish to use `try`/`finally` to put things back into the pool even if you leave through failure
- if the object might **unnecessarily** keep large graphs of sub-objects "reachable" (in terms of GC), you should ensure that any references are wiped before putting an object into the pool
- if the object implements `IResettable`, the pool will automatically call the `Reset()` method for you before storing items in the pool

A second API is exposed for use with value-types; there are a lot of scenarios in which you have some state that you need to expose
to an API that takes `object` - especially with callbacks like `WaitCallback`, `SendOrPostCallback`, `Action<object>`, etc. The data
will only be unboxed once at the receiver - so: rather than use a *regular* box, we can *rent* a box. Also, if you have multiple items of
state that you need to convey - consider a value-tuple.

``` c#
int id = ...
string name = ...
var obj = Pool.Box((id, name));
// ... probably pass obj to a callback-API
```

then later:

``` c#
(var id, var name) = Pool.UnboxAndReturn<(int, string)>(obj);
// use id/name as usual
```

It is the caller's responsibility to only access the state once.

The pool is global (`static`) and pretty modest in size. You can control it *a bit* by adding `[PoolSize(...)]` to the custom
classes and value-types that you use.


