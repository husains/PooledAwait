﻿using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using PooledAwait.Internal;

#if !NETSTANDARD1_3
using System.Reflection;
#endif

namespace PooledAwait
{
    /// <summary>
    /// Lightweight implementation of TaskCompletionSource<typeparamref name="T"/>
    /// </summary>
    /// <remarks>When possible, this will bypass TaskCompletionSource<typeparamref name="T"/> completely</remarks>
    public readonly struct ValueTaskCompletionSource<T>
    {
        /// <summary><see cref="Object.Equals(Object)"/></summary>
        public override bool Equals(object? obj) => obj is ValueTaskCompletionSource<T> other && _state == other._state;
        /// <summary><see cref="Object.GetHashCode"/></summary>
        public override int GetHashCode() => _state == null ? 0 : _state.GetHashCode();
        /// <summary><see cref="Object.ToString"/></summary>
        public override string ToString() => "ValueTaskCompletionSource";

#if !NETSTANDARD1_3
        private static readonly Func<Task<T>, Exception, bool>? s_TrySetException = TryCreate<Exception>(nameof(TrySetException));
        private static readonly Func<Task<T>, T, bool>? s_TrySetResult = TryCreate<T>(nameof(TrySetResult));
        private static readonly Func<Task<T>, CancellationToken, bool>? s_TrySetCanceled = TryCreate<CancellationToken>(nameof(TrySetCanceled));
        private static readonly bool s_Optimized = ValidateOptimized();
#endif
        private readonly object _state;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ValueTaskCompletionSource(object state) => _state = state;

        /// <summary>
        /// Gets the instance as a task
        /// </summary>
        public Task<T> Task
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _state as Task<T> ?? ((TaskCompletionSource<T>)_state).Task;
        }

        /// <summary>
        /// Indicates whether this is an invalid default instance
        /// </summary>
        public bool IsNull
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _state == null;
        }

        internal bool IsOptimized => _state is Task<T>;

        /// <summary>
        /// Create an instance pointing to a new task
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ValueTaskCompletionSource<T> Create() =>
#if !NETSTANDARD1_3
            s_Optimized ? CreateOptimized() :
#endif
            CreateFallback();

#if !NETSTANDARD1_3
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ValueTaskCompletionSource<T> CreateOptimized()
        {
            Counters.TaskAllocated.Increment();
            return new ValueTaskCompletionSource<T>(
                System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Task<T>)));
        }
#endif

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ValueTaskCompletionSource<T> CreateFallback()
        {
            Counters.TaskAllocated.Increment();
            return new ValueTaskCompletionSource<T>(new TaskCompletionSource<T>());
        }

        /// <summary>
        /// Set the outcome of the operation
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TrySetException(Exception exception)
        {
#if !NETSTANDARD1_3
            if (_state is Task<T> task)
            {
                var result = s_TrySetException!(task, exception);
                if (!result && !task.IsCompleted) SpinUntilCompleted(task);
                return result;
            }
#endif
            return _state != null && ((TaskCompletionSource<T>)_state).TrySetException(exception);
        }

        /// <summary>
        /// Set the result of the operation
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetException(Exception exception)
        {
            if (!TrySetException(exception)) ThrowHelper.ThrowInvalidOperationException();
        }

        /// <summary>
        /// Set the outcome of the operation
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TrySetCanceled(CancellationToken cancellationToken = default)
        {
#if !NETSTANDARD1_3
            if (_state is Task<T> task)
            {
                var result = s_TrySetCanceled!(task, cancellationToken);
                if (!result && !task.IsCompleted) SpinUntilCompleted(task);
                return result;
            }
#endif
            return _state != null && ((TaskCompletionSource<T>)_state).TrySetCanceled(
#if !NET45
                cancellationToken
#endif
                );
        }

        /// <summary>
        /// Set the result of the operation
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetCanceled(CancellationToken cancellationToken = default)
        {
            if (!TrySetCanceled(cancellationToken)) ThrowHelper.ThrowInvalidOperationException();
        }

        /// <summary>
        /// Set the outcome of the operation
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TrySetResult(T value)
        {
#if !NETSTANDARD1_3
            if (_state is Task<T> task)
            {
                var result = s_TrySetResult!(task, value);
                if (!result && !task.IsCompleted) SpinUntilCompleted(task);
                return result;
            }
#endif
            return _state != null && ((TaskCompletionSource<T>)_state).TrySetResult(value);
        }

        /// <summary>
        /// Set the result of the operation
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetResult(T value)
        {
            if (!TrySetResult(value)) ThrowHelper.ThrowInvalidOperationException();
        }

#if !NETSTANDARD1_3
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Func<Task<T>, TArg, bool>? TryCreate<TArg>(string methodName)
        {
            try
            {
                return (Func<Task<T>, TArg, bool>)Delegate.CreateDelegate(
                typeof(Func<Task<T>, TArg, bool>),
                typeof(Task<T>).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(TArg) }, null)!);
            }
            catch { return null; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool ValidateOptimized()
        {
            try
            {
                // perform feature tests of our voodoo
                var source = CreateOptimized();
                var task = source.Task;
                if (task == null) return false;
                if (task.IsCompleted) return false;

                if (!source.TrySetResult(default!)) return false;
                if (task.Status != TaskStatus.RanToCompletion) return false;

                source = CreateOptimized();
                task = source.Task;
                if (!source.TrySetException(new InvalidOperationException())) return false;
                if (!task.IsCompleted) return false;
                if (!task.IsFaulted) return false;
                try
                {
                    _ = task.Result;
                    return false;
                }
                catch (AggregateException ex) when (ex.InnerException is InvalidOperationException) { }
                if (!(task.Exception?.InnerException is InvalidOperationException)) return false;
                return true;
            }
            catch { return false; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void SpinUntilCompleted(Task<T> task)
        {
            // Spin wait until the completion is finalized by another thread.
            var sw = new SpinWait();
            while (!task.IsCompleted)
                sw.SpinOnce();
        }
#endif
    }
}
