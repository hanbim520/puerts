/*
* Tencent is pleased to support the open source community by making Puerts available.
* Copyright (C) 2020 Tencent.  All rights reserved.
* Puerts is licensed under the BSD 3-Clause License, except for the third-party components listed in the file 'LICENSE' which may be subject to their corresponding license terms. 
* This file is subject to the terms and conditions defined in file 'LICENSE', which is part of this source code package.
*/

#if PUERTS_DISABLE_IL2CPP_OPTIMIZATION || (!PUERTS_IL2CPP_OPTIMIZATION && UNITY_IPHONE) || !ENABLE_IL2CPP

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
#if !THREAD_SAFE
using System.Threading;
#endif

namespace Puerts
{
    internal class DelegateCreatorTree
    {
        private class Node
        {
            public Func<Type, IntPtr, Delegate> Creator;
            public Dictionary<Type, Node> Branchs = new Dictionary<Type, Node>();
        }

        private readonly Node root = new Node();

        private Node FindNode(Type[] types, bool createIfNotExisted)
        {
            Node cur = root;
            Node next;

            for (int i = 0; i < types.Length; i++)
            {
                if (!cur.Branchs.TryGetValue(types[i], out next))
                {
                    if (createIfNotExisted)
                    {
                        next = new Node();
                        cur.Branchs.Add(types[i], next);
                    }
                    else
                    {
                        return null;
                    }
                }
                cur = next;
            }
            return cur;
        }

        public void Add(Func<Type, IntPtr, Delegate> creator, params Type[] types)
        {
            Node node = FindNode(types, true);
            node.Creator = creator;
        }

        public Func<Type, IntPtr, Delegate> Find(Type[] types)
        {
            Node node = FindNode(types, false);
            return node == null ? null : node.Creator;
        }
    }

    internal class GenericDelegateFactory
    {
        readonly JsEnv jsEnv;

        //无返回值泛型方法
        MethodInfo[] genericAction = null;
        //有返回值泛型方法
        MethodInfo[] genericFunc = null;

        //泛型delegate适配器构造器的缓存
        Dictionary<Type, Func<Type, IntPtr, Delegate>> genericDelegateCreatorCache
            = new Dictionary<Type, Func<Type, IntPtr, Delegate>>();

        internal GenericDelegateFactory(JsEnv jsEnv)
        {
            this.jsEnv = jsEnv;
        }

        //Prevent unity il2cpp code stripping
        static void PreventStripping(object obj)
        {
            if (obj != null)
            {
                var gd = new GenericDelegate(IntPtr.Zero, null, null);
                gd.Action();
                gd.Action(obj);
                gd.Action(obj, obj);
                gd.Action(obj, obj, obj);
                gd.Action(obj, obj, obj, obj);

                gd.Func<object>();
                gd.Func<object, object>(obj);
                gd.Func<object, object, object>(obj, obj);
                gd.Func<object, object, object, object>(obj, obj, obj);
                gd.Func<object, object, object, object, object>(obj, obj, obj, obj);
            }
        }

        static GenericDelegateFactory()
        {
            PrimitiveTypeTranslate.Init();
        }

        Dictionary<IntPtr, WeakReference> nativePtrToGenericDelegate = new Dictionary<IntPtr, WeakReference>();

        internal GenericDelegate ToGenericDelegate(IntPtr ptr)
        {
            WeakReference maybeOne;
            if (nativePtrToGenericDelegate.TryGetValue(ptr, out maybeOne) && maybeOne.IsAlive)
            {
                return maybeOne.Target as GenericDelegate;
            }

            string stacktrace = null;
#if UNITY_EDITOR || DEBUG
            //stacktrace = jsEnv.Eval<string>("new Error().stack");
            stacktrace = PuertsDLL.GetJSStackTrace(jsEnv.isolate);
#endif
            GenericDelegate genericDelegate = new GenericDelegate(ptr, jsEnv, stacktrace);
            nativePtrToGenericDelegate[ptr] = new WeakReference(genericDelegate);
            return genericDelegate;
        }

        public void RemoveGenericDelegate(IntPtr ptr) 
        {
            WeakReference maybeOne;
            if (nativePtrToGenericDelegate.TryGetValue(ptr, out maybeOne) && !maybeOne.IsAlive) {
                nativePtrToGenericDelegate.Remove(ptr);
            }
        }

        public void CloseAll()
        {
            foreach (var referKV in nativePtrToGenericDelegate) {
                var refer = referKV.Value;
                if (refer.IsAlive) {
                    (refer.Target as GenericDelegate).Close();
                }
            }
        }

        internal bool IsJsFunctionAlive(IntPtr ptr)
        {
            WeakReference maybeOne;
            return nativePtrToGenericDelegate.TryGetValue(ptr, out maybeOne) && maybeOne.IsAlive;
        }

        Delegate CreateDelegate(Type type, GenericDelegate genericDelegate, MethodInfo method)
        {
            Delegate ret;
            if (genericDelegate.TryGetDelegate(type, out ret))
            {
                return ret;
            }
            else
            {
                ret = Delegate.CreateDelegate(type, genericDelegate, method);
                genericDelegate.AddDelegate(type, ret);
                return ret;
            }
        }

        internal Delegate Create(Type delegateType, IntPtr nativeJsFuncPtr)
        {
            Func<Type, IntPtr, Delegate> genericDelegateCreator;
            if (!genericDelegateCreatorCache.TryGetValue(delegateType, out genericDelegateCreator))
            {
                //如果泛型方法数组未初始化
                if (genericAction == null)
                {
                    PreventStripping(null);
                    var methods = typeof(GenericDelegate).GetMethods(BindingFlags.Instance | BindingFlags.Public
                        | BindingFlags.DeclaredOnly);
                    genericAction = methods.Where(m => m.Name == "Action").OrderBy(m => m.GetParameters().Length)
                        .ToArray();
                    genericFunc = methods.Where(m => m.Name == "Func").OrderBy(m => m.GetParameters().Length).ToArray();
                }

                MethodInfo delegateMethod = delegateType.GetMethod("Invoke");
                var parameters = delegateMethod.GetParameters();
                var typeArgs = parameters.Select(pinfo => pinfo.ParameterType).ToArray();

                if (delegateMethod.ReturnType == typeof(void))
                {
                    if (parameters.Length == 0)
                    {
                        //对无参无返回值特殊处理
                        var methodInfo = genericAction[0];
                        genericDelegateCreator = (dt, ptr) => CreateDelegate(dt, ToGenericDelegate(ptr), methodInfo);
                    }
                    else
                    {
                        genericDelegateCreator = ActionCreatorTree.Find(typeArgs);
                    }
                }
                else
                {
                    //如果是有返回值，需要加上返回值作为泛型实参
                    typeArgs = typeArgs.Concat(new Type[] { delegateMethod.ReturnType }).ToArray();
                    genericDelegateCreator = FuncCreatorTree.Find(typeArgs);
                }

                if (genericDelegateCreator == null)
                {
#if UNITY_EDITOR && (PUERTS_DISABLE_IL2CPP_OPTIMIZATION || (!PUERTS_IL2CPP_OPTIMIZATION && UNITY_IPHONE))
                    if ((delegateMethod.ReturnType.IsValueType && delegateMethod.ReturnType != typeof(void))
                        || parameters.Length > 4
                        || typeArgs.Any(paramType => paramType.IsValueType || paramType.IsByRef)
                        )
                    {
                        // 如果不在支持的范围，则生成一个永远返回空的构造器
                        genericDelegateCreator = (dt, x) => null;
                    }
                    else
#endif
                    {
                        //根据参数个数，返回值找到泛型实现
                        MethodInfo genericMethodInfo = null;
                        if (delegateMethod.ReturnType == typeof(void))
                        {
                            genericMethodInfo = genericAction[parameters.Length];
                        }
                        else
                        {
                            genericMethodInfo = genericFunc[parameters.Length];
                        }
                        //实例化泛型方法
                        var methodInfo = genericMethodInfo.MakeGenericMethod(typeArgs);
                        //构造器
                        genericDelegateCreator = (dt, ptr) => CreateDelegate(dt, ToGenericDelegate(ptr), methodInfo);
                    }
                }
                //缓存构造器，下次调用直接返回
                genericDelegateCreatorCache[delegateType] = genericDelegateCreator;
            }
            //创建delegate
            return genericDelegateCreator(delegateType, nativeJsFuncPtr);
        }

        DelegateCreatorTree ActionCreatorTree = new DelegateCreatorTree();

        DelegateCreatorTree FuncCreatorTree = new DelegateCreatorTree();

        public void RegisterAction<T1>()
        {
            ActionCreatorTree.Add((type, ptr) =>
            {
                GenericDelegate genericDelegate = ToGenericDelegate(ptr);
                return CreateDelegate(type, genericDelegate, new Action<T1>(genericDelegate.Action<T1>).Method);
            }, typeof(T1));
        }

        public void RegisterAction<T1, T2>()
        {
            ActionCreatorTree.Add((type, ptr) =>
            {
                GenericDelegate genericDelegate = ToGenericDelegate(ptr);
                return CreateDelegate(type, genericDelegate, new Action<T1, T2>(genericDelegate.Action<T1, T2>).Method);
            }, typeof(T1), typeof(T2));
        }

        public void RegisterAction<T1, T2, T3>()
        {
            ActionCreatorTree.Add((type, ptr) =>
            {
                GenericDelegate genericDelegate = ToGenericDelegate(ptr);
                return CreateDelegate(type, genericDelegate, new Action<T1, T2, T3>(genericDelegate.Action<T1, T2, T3>).Method);
            }, typeof(T1), typeof(T2), typeof(T3));
        }

        public void RegisterAction<T1, T2, T3, T4>()
        {
            ActionCreatorTree.Add((type, ptr) =>
            {
                GenericDelegate genericDelegate = ToGenericDelegate(ptr);
                return CreateDelegate(type, genericDelegate, new Action<T1, T2, T3, T4>(genericDelegate.Action<T1, T2, T3, T4>).Method);
            }, typeof(T1), typeof(T2), typeof(T3), typeof(T4));
        }

        public void RegisterFunc<TResult>()
        {
            FuncCreatorTree.Add((type, ptr) =>
            {
                GenericDelegate genericDelegate = ToGenericDelegate(ptr);
                return CreateDelegate(type, genericDelegate, new Func<TResult>(genericDelegate.Func<TResult>).Method);
            }, typeof(TResult));
        }

        public void RegisterFunc<T1, TResult>()
        {
            FuncCreatorTree.Add((type, ptr) =>
            {
                GenericDelegate genericDelegate = ToGenericDelegate(ptr);
                return CreateDelegate(type, genericDelegate, new Func<T1, TResult>(genericDelegate.Func<T1, TResult>).Method);
            }, typeof(T1), typeof(TResult));
        }

        public void RegisterFunc<T1, T2, TResult>()
        {
            FuncCreatorTree.Add((type, ptr) =>
            {
                GenericDelegate genericDelegate = ToGenericDelegate(ptr);
                return CreateDelegate(type, genericDelegate, new Func<T1, T2, TResult>(genericDelegate.Func<T1, T2, TResult>).Method);
            }, typeof(T1), typeof(T2), typeof(TResult));
        }

        public void RegisterFunc<T1, T2, T3, TResult>()
        {
            FuncCreatorTree.Add((type, ptr) =>
            {
                GenericDelegate genericDelegate = ToGenericDelegate(ptr);
                return CreateDelegate(type, genericDelegate, new Func<T1, T2, T3, TResult>(genericDelegate.Func<T1, T2, T3, TResult>).Method);
            }, typeof(T1), typeof(T2), typeof(T3), typeof(TResult));
        }

        public void RegisterFunc<T1, T2, T3, T4, TResult>()
        {
            FuncCreatorTree.Add((type, ptr) =>
            {
                GenericDelegate genericDelegate = ToGenericDelegate(ptr);
                return CreateDelegate(type, genericDelegate, new Func<T1, T2, T3, T4, TResult>(genericDelegate.Func<T1, T2, T3, T4, TResult>).Method);
            }, typeof(T1), typeof(T2), typeof(T3), typeof(T4), typeof(TResult));
        }
    }

    //泛型适配器
    public class GenericDelegate
    {
        private JsEnv jsEnv;
        private IntPtr nativeJsFuncPtr;
        private IntPtr isolate;

        private Type firstKey = null;
        private Delegate firstValue = null;
        private Dictionary<Type, Delegate> bindTo = null;

#if UNITY_EDITOR || DEBUG
        private string stacktrace;
#endif
#if !THREAD_SAFE
        static Thread s_MainThread = Thread.CurrentThread;
#endif

        internal IntPtr getJsFuncPtr() 
        {
            return nativeJsFuncPtr;
        }

        internal GenericDelegate(IntPtr nativeJsFuncPtr, JsEnv jsEnv, string stacktrace)
        {
            this.nativeJsFuncPtr = nativeJsFuncPtr;
            jsEnv.IncFuncRef(nativeJsFuncPtr);
            isolate = jsEnv != null ? jsEnv.isolate : IntPtr.Zero;
            this.jsEnv = jsEnv;
#if UNITY_EDITOR || DEBUG
            this.stacktrace = stacktrace;
#endif
        }

        internal void Close()
        {
            nativeJsFuncPtr = IntPtr.Zero;
            // it should set to null, otherwise it will prevent JsEnv to be GC.
            jsEnv = null; 
        }

        private void CheckLiveness(bool shouldThrow = true)
        {
            if (nativeJsFuncPtr == IntPtr.Zero)
            {
#if UNITY_EDITOR || DEBUG
                if (shouldThrow) throw new Exception("JsEnv has been disposed, stacktrace:" + (string.IsNullOrEmpty(this.stacktrace) ? "unknown" : this.stacktrace));
#else
                if (shouldThrow) throw new Exception("JsEnv has been disposed");
#endif
            }
            else 
            {
                jsEnv.CheckLiveness();
            }
        }
        
        private void CheckThread()
        {
#if !THREAD_SAFE
            if (Thread.CurrentThread != s_MainThread)
            {
#if UNITY_EDITOR || DEBUG
                throw new Exception("GenericDelegate should only be used in main thread, stacktrace:" + (string.IsNullOrEmpty(this.stacktrace) ? "unknown" : this.stacktrace));
#else
                throw new Exception("GenericDelegate should only be used in main thread");
#endif
#endif
            }
        }

        ~GenericDelegate() 
        {
            if (nativeJsFuncPtr == IntPtr.Zero) return;
#if THREAD_SAFE
            lock(jsEnv) {
#endif
            if (jsEnv.CheckLiveness(false))
            {
                jsEnv.DecFuncRef(nativeJsFuncPtr);
            }
#if THREAD_SAFE
            }
#endif
        }

        public bool TryGetDelegate(Type key, out Delegate value)
        {
            if (key == firstKey)
            {
                value = firstValue;
                return true;
            }
            if (bindTo != null)
            {
                return bindTo.TryGetValue(key, out value);
            }
            value = null;
            return false;
        }

        public void AddDelegate(Type key, Delegate value)
        {
            if (key == firstKey)
            {
                throw new ArgumentException("An element with the same key already exists in the dictionary.");
            }

            if (firstKey == null && bindTo == null) // nothing 
            {
                firstKey = key;
                firstValue = value;
            }
            else if (firstKey != null && bindTo == null) // one key existed
            {
                bindTo = new Dictionary<Type, Delegate>();
                bindTo.Add(firstKey, firstValue);
                firstKey = null;
                firstValue = null;
                bindTo.Add(key, value);
            }
            else
            {
                bindTo.Add(key, value);
            }
        }

        public void Action()
        {
            CheckThread();
            CheckLiveness();
#if THREAD_SAFE
            lock(jsEnv) {
#endif
            IntPtr resultInfo = PuertsDLL.InvokeJSFunction(nativeJsFuncPtr, false);
            if (resultInfo == IntPtr.Zero)
            {
                string exceptionInfo = PuertsDLL.GetFunctionLastExceptionInfo(nativeJsFuncPtr);
                throw new Exception(exceptionInfo);
            }
#if THREAD_SAFE
            }
#endif
        }

        public void Action<T1>(T1 p1)
        {
            CheckThread();
            CheckLiveness();
#if THREAD_SAFE
            lock(jsEnv) {
#endif
            StaticTranslate<T1>.Set(jsEnv.Idx, isolate, NativeValueApi.SetValueToArgument, nativeJsFuncPtr, p1);
            IntPtr resultInfo = PuertsDLL.InvokeJSFunction(nativeJsFuncPtr, false);
            if (resultInfo == IntPtr.Zero)
            {
                string exceptionInfo = PuertsDLL.GetFunctionLastExceptionInfo(nativeJsFuncPtr);
                throw new Exception(exceptionInfo);
            }
#if THREAD_SAFE
            }
#endif
        }

        public void Action<T1, T2>(T1 p1, T2 p2) 
        {
            CheckThread();
            CheckLiveness();
#if THREAD_SAFE
            lock(jsEnv) {
#endif
            StaticTranslate<T1>.Set(jsEnv.Idx, isolate, NativeValueApi.SetValueToArgument, nativeJsFuncPtr, p1);
            StaticTranslate<T2>.Set(jsEnv.Idx, isolate, NativeValueApi.SetValueToArgument, nativeJsFuncPtr, p2);
            IntPtr resultInfo = PuertsDLL.InvokeJSFunction(nativeJsFuncPtr, false);
            if (resultInfo == IntPtr.Zero)
            {
                string exceptionInfo = PuertsDLL.GetFunctionLastExceptionInfo(nativeJsFuncPtr);
                throw new Exception(exceptionInfo);
            }
#if THREAD_SAFE
            }
#endif
        }

        public void Action<T1, T2, T3>(T1 p1, T2 p2, T3 p3)
        {
            CheckThread();
            CheckLiveness();
#if THREAD_SAFE
            lock(jsEnv) {
#endif
            StaticTranslate<T1>.Set(jsEnv.Idx, isolate, NativeValueApi.SetValueToArgument, nativeJsFuncPtr, p1);
            StaticTranslate<T2>.Set(jsEnv.Idx, isolate, NativeValueApi.SetValueToArgument, nativeJsFuncPtr, p2);
            StaticTranslate<T3>.Set(jsEnv.Idx, isolate, NativeValueApi.SetValueToArgument, nativeJsFuncPtr, p3);
            IntPtr resultInfo = PuertsDLL.InvokeJSFunction(nativeJsFuncPtr, false);
            if (resultInfo == IntPtr.Zero)
            {
                string exceptionInfo = PuertsDLL.GetFunctionLastExceptionInfo(nativeJsFuncPtr);
                throw new Exception(exceptionInfo);
            }
#if THREAD_SAFE
            }
#endif
        }

        public void Action<T1, T2, T3, T4>(T1 p1, T2 p2, T3 p3, T4 p4)
        {
            CheckThread();
            CheckLiveness();
#if THREAD_SAFE
            lock(jsEnv) {
#endif
            StaticTranslate<T1>.Set(jsEnv.Idx, isolate, NativeValueApi.SetValueToArgument, nativeJsFuncPtr, p1);
            StaticTranslate<T2>.Set(jsEnv.Idx, isolate, NativeValueApi.SetValueToArgument, nativeJsFuncPtr, p2);
            StaticTranslate<T3>.Set(jsEnv.Idx, isolate, NativeValueApi.SetValueToArgument, nativeJsFuncPtr, p3);
            StaticTranslate<T4>.Set(jsEnv.Idx, isolate, NativeValueApi.SetValueToArgument, nativeJsFuncPtr, p4);
            IntPtr resultInfo = PuertsDLL.InvokeJSFunction(nativeJsFuncPtr, false);
            if (resultInfo == IntPtr.Zero)
            {
                string exceptionInfo = PuertsDLL.GetFunctionLastExceptionInfo(nativeJsFuncPtr);
                throw new Exception(exceptionInfo);
            }
#if THREAD_SAFE
            }
#endif
        }

        public TResult Func<TResult>()
        {
            CheckThread();
            CheckLiveness();
#if THREAD_SAFE
            lock(jsEnv) {
#endif
            IntPtr resultInfo = PuertsDLL.InvokeJSFunction(nativeJsFuncPtr, true);
            if (resultInfo == IntPtr.Zero)
            {
                string exceptionInfo = PuertsDLL.GetFunctionLastExceptionInfo(nativeJsFuncPtr);
                throw new Exception(exceptionInfo);
            }
            TResult result = StaticTranslate<TResult>.Get(jsEnv.Idx, isolate, NativeValueApi.GetValueFromResult, resultInfo, false);
            PuertsDLL.ResetResult(resultInfo);
            return result;
#if THREAD_SAFE
            }
#endif
        }

        public TResult Func<T1, TResult>(T1 p1)
        {
            CheckThread();
            CheckLiveness();
#if THREAD_SAFE
            lock(jsEnv) {
#endif
            StaticTranslate<T1>.Set(jsEnv.Idx, isolate, NativeValueApi.SetValueToArgument, nativeJsFuncPtr, p1);
            IntPtr resultInfo = PuertsDLL.InvokeJSFunction(nativeJsFuncPtr, true);
            if (resultInfo == IntPtr.Zero)
            {
                string exceptionInfo = PuertsDLL.GetFunctionLastExceptionInfo(nativeJsFuncPtr);
                throw new Exception(exceptionInfo);
            }
            TResult result = StaticTranslate<TResult>.Get(jsEnv.Idx, isolate, NativeValueApi.GetValueFromResult, resultInfo, false);
            PuertsDLL.ResetResult(resultInfo);
            return result;
#if THREAD_SAFE
            }
#endif
        }

        public TResult Func<T1, T2, TResult>(T1 p1, T2 p2)
        {
            CheckThread();
            CheckLiveness();
#if THREAD_SAFE
            lock(jsEnv) {
#endif
            StaticTranslate<T1>.Set(jsEnv.Idx, isolate, NativeValueApi.SetValueToArgument, nativeJsFuncPtr, p1);
            StaticTranslate<T2>.Set(jsEnv.Idx, isolate, NativeValueApi.SetValueToArgument, nativeJsFuncPtr, p2);
            IntPtr resultInfo = PuertsDLL.InvokeJSFunction(nativeJsFuncPtr, true);
            if (resultInfo == IntPtr.Zero)
            {
                string exceptionInfo = PuertsDLL.GetFunctionLastExceptionInfo(nativeJsFuncPtr);
                throw new Exception(exceptionInfo);
            }
            TResult result = StaticTranslate<TResult>.Get(jsEnv.Idx, isolate, NativeValueApi.GetValueFromResult, resultInfo, false);
            PuertsDLL.ResetResult(resultInfo);
            return result;
#if THREAD_SAFE
            }
#endif
        }

        public TResult Func<T1, T2, T3, TResult>(T1 p1, T2 p2, T3 p3)
        {
            CheckThread();
            CheckLiveness();
#if THREAD_SAFE
            lock(jsEnv) {
#endif
            StaticTranslate<T1>.Set(jsEnv.Idx, isolate, NativeValueApi.SetValueToArgument, nativeJsFuncPtr, p1);
            StaticTranslate<T2>.Set(jsEnv.Idx, isolate, NativeValueApi.SetValueToArgument, nativeJsFuncPtr, p2);
            StaticTranslate<T3>.Set(jsEnv.Idx, isolate, NativeValueApi.SetValueToArgument, nativeJsFuncPtr, p3);
            IntPtr resultInfo = PuertsDLL.InvokeJSFunction(nativeJsFuncPtr, true);
            if (resultInfo == IntPtr.Zero)
            {
                string exceptionInfo = PuertsDLL.GetFunctionLastExceptionInfo(nativeJsFuncPtr);
                throw new Exception(exceptionInfo);
            }
            TResult result = StaticTranslate<TResult>.Get(jsEnv.Idx, isolate, NativeValueApi.GetValueFromResult, resultInfo, false);
            PuertsDLL.ResetResult(resultInfo);
            return result;
#if THREAD_SAFE
            }
#endif
        }

        public TResult Func<T1, T2, T3, T4, TResult>(T1 p1, T2 p2, T3 p3, T4 p4)
        {
            CheckThread();
            CheckLiveness();
#if THREAD_SAFE
            lock(jsEnv) {
#endif
            StaticTranslate<T1>.Set(jsEnv.Idx, isolate, NativeValueApi.SetValueToArgument, nativeJsFuncPtr, p1);
            StaticTranslate<T2>.Set(jsEnv.Idx, isolate, NativeValueApi.SetValueToArgument, nativeJsFuncPtr, p2);
            StaticTranslate<T3>.Set(jsEnv.Idx, isolate, NativeValueApi.SetValueToArgument, nativeJsFuncPtr, p3);
            StaticTranslate<T4>.Set(jsEnv.Idx, isolate, NativeValueApi.SetValueToArgument, nativeJsFuncPtr, p4);
            IntPtr resultInfo = PuertsDLL.InvokeJSFunction(nativeJsFuncPtr, true);
            if (resultInfo == IntPtr.Zero)
            {
                string exceptionInfo = PuertsDLL.GetFunctionLastExceptionInfo(nativeJsFuncPtr);
                throw new Exception(exceptionInfo);
            }
            TResult result = StaticTranslate<TResult>.Get(jsEnv.Idx, isolate, NativeValueApi.GetValueFromResult, resultInfo, false);
            PuertsDLL.ResetResult(resultInfo);
            return result;
#if THREAD_SAFE
            }
#endif
        }
    }
}

#endif
