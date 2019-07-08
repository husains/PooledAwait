﻿using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PooledAwait
{
    /// <summary>
    /// Lightweight implementation of TaskCompletionSource<typeparamref name="T"/>
    /// </summary>
    /// <remarks>When possible, this will bypass TaskCompletionSource<typeparamref name="T"/> completely</remarks>
    public readonly struct ValueTaskCompletionSource<T>
    {
        private static readonly Func<Task<T>, Exception, bool>? s_TrySetException = TryCreate<Exception>(nameof(TrySetException));
        private static readonly Func<Task<T>, T, bool>? s_TrySetResult = TryCreate<T>(nameof(TrySetResult));
        private static readonly bool s_Optimized = ValidateOptimized();
        private readonly object _state;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ValueTaskCompletionSource(object state) => _state = state;

        /// <summary>
        /// Gets the instance as a task
        /// </summary>
        public Task<T> Task
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _state is Task<T> t ? t : ((TaskCompletionSource<T>)_state).Task;
        }

        /// <summary>
        /// Indicates whether this instance is well-defined against a task instance
        /// </summary>
        public bool HasTask
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _state != null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void SpinUntilCompleted(Task<T> task)
        {
            // Spin wait until the completion is finalized by another thread.
            var sw = new SpinWait();
            while (!task.IsCompleted)
                sw.SpinOnce();
        }

        /// <summary>
        /// Create an instance pointing to a new task
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ValueTaskCompletionSource<T> Create() => s_Optimized ? CreateOptimized() : CreateFallback();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ValueTaskCompletionSource<T> CreateOptimized() => new ValueTaskCompletionSource<T>(FormatterServices.GetUninitializedObject(typeof(Task<T>)));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ValueTaskCompletionSource<T> CreateFallback() => new ValueTaskCompletionSource<T>(new TaskCompletionSource<T>());

        /// <summary>
        /// Set the outcome of the operation
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TrySetException(Exception exception)
        {
            if (_state is Task<T> task)
            {
                var result = s_TrySetException!(task, exception);
                if (!result && !task.IsCompleted) SpinUntilCompleted(task);
                return result;
            }
            else
            {
                return ((TaskCompletionSource<T>)_state).TrySetException(exception);
            }
        }

        /// <summary>
        /// Set the outcome of the operation
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TrySetResult(T value)
        {
            if (_state is Task<T> task)
            {
                var result = s_TrySetResult!(task, value);
                if (!result && !task.IsCompleted) SpinUntilCompleted(task);
                return result;
            }
            else
            {
                return ((TaskCompletionSource<T>)_state).TrySetResult(value);
            }
        }

        private static Func<Task<T>, TArg, bool>? TryCreate<TArg>(string methodName)
        {
            try
            {
                return (Func<Task<T>, TArg, bool>)Delegate.CreateDelegate(
                typeof(Func<Task<T>, TArg, bool>),
                typeof(Task<T>).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(TArg) }, null));
            }
            catch { return null; }
        }

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
                if (!task.IsCompleted) return false;
                if (!task.IsCompletedSuccessfully) return false;

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
                if (!(task.Exception.InnerException is InvalidOperationException)) return false;
                return true;
            }
            catch { return false; }
        }

    }
}