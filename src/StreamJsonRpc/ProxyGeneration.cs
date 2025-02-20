﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

// Uncomment the SaveAssembly symbol and run one test to save the generated DLL for inspection in ILSpy as part of debugging.
#if NETFRAMEWORK
////#define SaveAssembly
#endif

namespace StreamJsonRpc
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Globalization;
    using System.Linq;
    using System.Reflection;
    using System.Reflection.Emit;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft;

    internal static class ProxyGeneration
    {
        private static readonly Type[] EmptyTypes = new Type[0];
        private static readonly AssemblyName ProxyAssemblyName = new AssemblyName(string.Format(CultureInfo.InvariantCulture, "StreamJsonRpc_Proxies_{0}", Guid.NewGuid()));
        private static readonly MethodInfo DelegateCombineMethod = typeof(Delegate).GetRuntimeMethod(nameof(Delegate.Combine), new Type[] { typeof(Delegate), typeof(Delegate) });
        private static readonly MethodInfo DelegateRemoveMethod = typeof(Delegate).GetRuntimeMethod(nameof(Delegate.Remove), new Type[] { typeof(Delegate), typeof(Delegate) });
        private static readonly MethodInfo CancellationTokenNonePropertyGetter = typeof(CancellationToken).GetRuntimeProperty(nameof(CancellationToken.None)).GetMethod;
        private static readonly AssemblyBuilder AssemblyBuilder;
        private static readonly ModuleBuilder ProxyModuleBuilder;
        private static readonly ConstructorInfo ObjectCtor = typeof(object).GetTypeInfo().DeclaredConstructors.Single();
        private static readonly Dictionary<TypeInfo, TypeInfo> GeneratedProxiesByInterface = new Dictionary<TypeInfo, TypeInfo>();
        private static readonly MethodInfo CompareExchangeMethod = (from method in typeof(Interlocked).GetRuntimeMethods()
                                                                    where method.Name == nameof(Interlocked.CompareExchange)
                                                                    let parameters = method.GetParameters()
                                                                    where parameters.Length == 3 && parameters.All(p => p.ParameterType.IsGenericParameter || p.ParameterType.GetTypeInfo().ContainsGenericParameters)
                                                                    select method).Single();

        private static readonly MethodInfo MethodNameTransformPropertyGetter = typeof(JsonRpcProxyOptions).GetRuntimeProperty(nameof(JsonRpcProxyOptions.MethodNameTransform)).GetMethod;
        private static readonly MethodInfo MethodNameTransformInvoke = typeof(Func<string, string>).GetRuntimeMethod(nameof(JsonRpcProxyOptions.MethodNameTransform.Invoke), new Type[] { typeof(string) });
        private static readonly MethodInfo EventNameTransformPropertyGetter = typeof(JsonRpcProxyOptions).GetRuntimeProperty(nameof(JsonRpcProxyOptions.EventNameTransform)).GetMethod;
        private static readonly MethodInfo EventNameTransformInvoke = typeof(Func<string, string>).GetRuntimeMethod(nameof(JsonRpcProxyOptions.EventNameTransform.Invoke), new Type[] { typeof(string) });
        private static readonly MethodInfo ServerRequiresNamedArgumentsPropertyGetter = typeof(JsonRpcProxyOptions).GetRuntimeProperty(nameof(JsonRpcProxyOptions.ServerRequiresNamedArguments)).GetMethod;

        static ProxyGeneration()
        {
#if SaveAssembly
            AssemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName($"rpcProxies_{Guid.NewGuid()}"), AssemblyBuilderAccess.RunAndSave);
#else
            AssemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName($"rpcProxies_{Guid.NewGuid()}"), AssemblyBuilderAccess.RunAndCollect);
#endif
            ProxyModuleBuilder = AssemblyBuilder.DefineDynamicModule("rpcProxies");
        }

        internal static TypeInfo Get(TypeInfo serviceInterface)
        {
            Requires.NotNull(serviceInterface, nameof(serviceInterface));
            VerifySupported(serviceInterface.IsInterface, Resources.ClientProxyTypeArgumentMustBeAnInterface, serviceInterface);

            TypeInfo generatedType;

            lock (GeneratedProxiesByInterface)
            {
                if (GeneratedProxiesByInterface.TryGetValue(serviceInterface, out generatedType))
                {
                    return generatedType;
                }
            }

            var methodNameMap = new JsonRpc.MethodNameMap(serviceInterface);

            lock (ProxyModuleBuilder)
            {
                var interfaces = new List<Type>
                {
                    serviceInterface.AsType(),
                };

                interfaces.Add(typeof(IJsonRpcClientProxy));

                var proxyTypeBuilder = ProxyModuleBuilder.DefineType(
                    string.Format(CultureInfo.InvariantCulture, "_proxy_{0}_{1}", serviceInterface.FullName, Guid.NewGuid()),
                    TypeAttributes.Public,
                    typeof(object),
                    interfaces.ToArray());
#if NETSTANDARD1_6
                Type proxyType = proxyTypeBuilder.AsType();
#else
                Type proxyType = proxyTypeBuilder;
#endif
                const FieldAttributes fieldAttributes = FieldAttributes.Private | FieldAttributes.InitOnly;
                var jsonRpcField = proxyTypeBuilder.DefineField("rpc", typeof(JsonRpc), fieldAttributes);
                var optionsField = proxyTypeBuilder.DefineField("options", typeof(JsonRpcProxyOptions), fieldAttributes);

                VerifySupported(!FindAllOnThisAndOtherInterfaces(serviceInterface, i => i.DeclaredProperties).Any(), Resources.UnsupportedPropertiesOnClientProxyInterface, serviceInterface);

                // Implement events
                var ctorActions = new List<Action<ILGenerator>>();
                foreach (var evt in FindAllOnThisAndOtherInterfaces(serviceInterface, i => i.DeclaredEvents))
                {
                    VerifySupported(evt.EventHandlerType.Equals(typeof(EventHandler)) || (evt.EventHandlerType.GetTypeInfo().IsGenericType && evt.EventHandlerType.GetGenericTypeDefinition().Equals(typeof(EventHandler<>))), Resources.UnsupportedEventHandlerTypeOnClientProxyInterface, evt);

                    // public event EventHandler EventName;
                    var evtBuilder = proxyTypeBuilder.DefineEvent(evt.Name, evt.Attributes, evt.EventHandlerType);

                    // private EventHandler eventName;
                    var evtField = proxyTypeBuilder.DefineField(evt.Name, evt.EventHandlerType, FieldAttributes.Private);

                    // add_EventName
                    var addRemoveHandlerParams = new Type[] { evt.EventHandlerType };
                    const MethodAttributes methodAttributes = MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.NewSlot | MethodAttributes.Virtual;
                    var addMethod = proxyTypeBuilder.DefineMethod($"add_{evt.Name}", methodAttributes, null, addRemoveHandlerParams);
                    ImplementEventAccessor(addMethod.GetILGenerator(), evtField, DelegateCombineMethod);
                    evtBuilder.SetAddOnMethod(addMethod);

                    // remove_EventName
                    var removeMethod = proxyTypeBuilder.DefineMethod($"remove_{evt.Name}", methodAttributes, null, addRemoveHandlerParams);
                    ImplementEventAccessor(removeMethod.GetILGenerator(), evtField, DelegateRemoveMethod);
                    evtBuilder.SetRemoveOnMethod(removeMethod);

                    // void OnEventName(EventArgs args)
                    var eventArgsType = evt.EventHandlerType.GetTypeInfo().GetDeclaredMethod(nameof(EventHandler.Invoke)).GetParameters()[1].ParameterType;
                    var raiseEventMethod = proxyTypeBuilder.DefineMethod(
                        $"On{evt.Name}",
                        MethodAttributes.HideBySig | MethodAttributes.Private,
                        null,
                        new Type[] { eventArgsType });
                    ImplementRaiseEventMethod(raiseEventMethod.GetILGenerator(), evtField, jsonRpcField);

                    ctorActions.Add(new Action<ILGenerator>(il =>
                    {
                        var addLocalRpcMethod = typeof(JsonRpc).GetRuntimeMethod(nameof(JsonRpc.AddLocalRpcMethod), new Type[] { typeof(string), typeof(Delegate) });
                        var delegateCtor = typeof(Action<>).MakeGenericType(eventArgsType).GetTypeInfo().DeclaredConstructors.Single();

                        // rpc.AddLocalRpcMethod("EventName", new Action<EventArgs>(this.OnEventName));
                        il.Emit(OpCodes.Ldarg_1); // .ctor's rpc parameter

                        // First argument to AddLocalRpcMethod is the method name.
                        // Run it through the method name transform.
                        // this.options.EventNameTransform.Invoke("clrOrAttributedMethodName")
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Ldfld, optionsField);
                        il.EmitCall(OpCodes.Callvirt, EventNameTransformPropertyGetter, null);
                        il.Emit(OpCodes.Ldstr, evt.Name);
                        il.EmitCall(OpCodes.Callvirt, EventNameTransformInvoke, null);

                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Ldftn, raiseEventMethod);
                        il.Emit(OpCodes.Newobj, delegateCtor);
                        il.Emit(OpCodes.Callvirt, addLocalRpcMethod);
                    }));
                }

                // .ctor(JsonRpc, JsonRpcProxyOptions)
                {
                    var ctor = proxyTypeBuilder.DefineConstructor(
                        MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                        CallingConventions.Standard,
                        new Type[] { typeof(JsonRpc), typeof(JsonRpcProxyOptions) });
                    var il = ctor.GetILGenerator();

                    // : base()
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Call, ObjectCtor);

                    // this.rpc = rpc;
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Stfld, jsonRpcField);

                    // this.options = options;
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Stfld, optionsField);

                    // Emit IL that supports events.
                    foreach (var action in ctorActions)
                    {
                        action(il);
                    }

                    il.Emit(OpCodes.Ret);
                }

                // IDisposable.Dispose()
                {
                    var disposeMethod = proxyTypeBuilder.DefineMethod(nameof(IDisposable.Dispose), MethodAttributes.Private | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual);
                    var il = disposeMethod.GetILGenerator();

                    // this.rpc.Dispose();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, jsonRpcField);
                    MethodInfo jsonRpcDisposeMethod = typeof(JsonRpc).GetTypeInfo().GetDeclaredMethods(nameof(JsonRpc.Dispose)).Single(m => m.GetParameters().Length == 0);
                    il.EmitCall(OpCodes.Callvirt, jsonRpcDisposeMethod, EmptyTypes);
                    il.Emit(OpCodes.Ret);

                    proxyTypeBuilder.DefineMethodOverride(disposeMethod, typeof(IDisposable).GetTypeInfo().GetDeclaredMethod(nameof(IDisposable.Dispose)));
                }

                // IJsonRpcClientProxy.JsonRpc property
                {
                    var jsonRpcProperty = proxyTypeBuilder.DefineProperty(
                        nameof(IJsonRpcClientProxy.JsonRpc),
                        PropertyAttributes.None,
                        typeof(JsonRpc),
                        parameterTypes: null);

                    // get_JsonRpc() method
                    var jsonRpcPropertyGetter = proxyTypeBuilder.DefineMethod(
                        "get_" + nameof(IJsonRpcClientProxy.JsonRpc),
                        MethodAttributes.Private | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual | MethodAttributes.SpecialName,
                        typeof(JsonRpc),
                        Type.EmptyTypes);
                    var il = jsonRpcPropertyGetter.GetILGenerator();

                    // return this.jsonRpc;
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, jsonRpcField);
                    il.Emit(OpCodes.Ret);

                    proxyTypeBuilder.DefineMethodOverride(jsonRpcPropertyGetter, typeof(IJsonRpcClientProxy).GetTypeInfo().GetDeclaredProperty(nameof(IJsonRpcClientProxy.JsonRpc)).GetMethod);
                    jsonRpcProperty.SetGetMethod(jsonRpcPropertyGetter);
                }

                var invokeAsyncMethodInfos = typeof(JsonRpc).GetTypeInfo().DeclaredMethods.Where(m => m.Name == nameof(JsonRpc.InvokeAsync) && m.GetParameters()[1].ParameterType == typeof(object[])).ToArray();
                var invokeAsyncOfTaskMethodInfo = invokeAsyncMethodInfos.Single(m => !m.IsGenericMethod);
                var invokeAsyncOfTaskOfTMethodInfo = invokeAsyncMethodInfos.Single(m => m.IsGenericMethod);

                var invokeWithCancellationAsyncMethodInfos = typeof(JsonRpc).GetTypeInfo().DeclaredMethods.Where(m => m.Name == nameof(JsonRpc.InvokeWithCancellationAsync));
                var invokeWithCancellationAsyncOfTaskMethodInfo = invokeWithCancellationAsyncMethodInfos.Single(m => !m.IsGenericMethod);
                var invokeWithCancellationAsyncOfTaskOfTMethodInfo = invokeWithCancellationAsyncMethodInfos.Single(m => m.IsGenericMethod);

                var invokeWithParameterObjectAsyncMethodInfos = typeof(JsonRpc).GetTypeInfo().DeclaredMethods.Where(m => m.Name == nameof(JsonRpc.InvokeWithParameterObjectAsync));
                var invokeWithParameterObjectAsyncOfTaskMethodInfo = invokeWithParameterObjectAsyncMethodInfos.Single(m => !m.IsGenericMethod);
                var invokeWithParameterObjectAsyncOfTaskOfTMethodInfo = invokeWithParameterObjectAsyncMethodInfos.Single(m => m.IsGenericMethod);

                foreach (var method in FindAllOnThisAndOtherInterfaces(serviceInterface, i => i.DeclaredMethods).Where(m => !m.IsSpecialName))
                {
                    bool returnTypeIsTask = method.ReturnType == typeof(Task) || (method.ReturnType.GetTypeInfo().IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>));
                    bool returnTypeIsValueTask = method.ReturnType == typeof(ValueTask) || (method.ReturnType.GetTypeInfo().IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(ValueTask<>));
                    VerifySupported(returnTypeIsTask || returnTypeIsValueTask, Resources.UnsupportedMethodReturnTypeOnClientProxyInterface, method, method.ReturnType.FullName);
                    VerifySupported(!method.IsGenericMethod, Resources.UnsupportedGenericMethodsOnClientProxyInterface, method);

                    ParameterInfo[] methodParameters = method.GetParameters();
                    var methodBuilder = proxyTypeBuilder.DefineMethod(
                        method.Name,
                        MethodAttributes.Private | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
                        method.ReturnType,
                        methodParameters.Select(p => p.ParameterType).ToArray());
                    var il = methodBuilder.GetILGenerator();

                    // this.rpc
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, jsonRpcField);

                    // First argument to InvokeAsync is the method name.
                    // Run it through the method name transform.
                    // this.options.MethodNameTransform.Invoke("clrOrAttributedMethodName")
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, optionsField);
                    il.EmitCall(OpCodes.Callvirt, MethodNameTransformPropertyGetter, null);
                    il.Emit(OpCodes.Ldstr, methodNameMap.GetRpcMethodName(method));
                    il.EmitCall(OpCodes.Callvirt, MethodNameTransformInvoke, null);

                    var positionalArgsLabel = il.DefineLabel();

                    ParameterInfo cancellationTokenParameter = methodParameters.FirstOrDefault(p => p.ParameterType == typeof(CancellationToken));
                    int argumentCountExcludingCancellationToken = methodParameters.Length - (cancellationTokenParameter != null ? 1 : 0);
                    VerifySupported(cancellationTokenParameter == null || cancellationTokenParameter.Position == methodParameters.Length - 1, Resources.CancellationTokenMustBeLastParameter, method);
                    bool hasReturnValue = method.ReturnType.GetTypeInfo().IsGenericType;

                    // if (this.options.ServerRequiresNamedArguments) {
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, optionsField);
                    il.EmitCall(OpCodes.Callvirt, ServerRequiresNamedArgumentsPropertyGetter, null);
                    il.Emit(OpCodes.Brfalse_S, positionalArgsLabel);

                    // The second argument is a single parameter object.
                    {
                        if (argumentCountExcludingCancellationToken > 0)
                        {
                            ConstructorInfo paramObjectCtor = CreateParameterObjectType(methodParameters.Take(argumentCountExcludingCancellationToken).ToArray(), proxyType);
                            for (int i = 0; i < argumentCountExcludingCancellationToken; i++)
                            {
                                il.Emit(OpCodes.Ldarg, i + 1);
                            }

                            il.Emit(OpCodes.Newobj, paramObjectCtor);
                        }
                        else
                        {
                            il.Emit(OpCodes.Ldnull);
                        }

                        MethodInfo invokingMethod = hasReturnValue
                            ? invokeWithParameterObjectAsyncOfTaskOfTMethodInfo.MakeGenericMethod(method.ReturnType.GetTypeInfo().GenericTypeArguments[0])
                            : invokeWithParameterObjectAsyncOfTaskMethodInfo;
                        if (cancellationTokenParameter != null)
                        {
                            il.Emit(OpCodes.Ldarg, cancellationTokenParameter.Position + 1);
                        }
                        else
                        {
                            il.Emit(OpCodes.Call, CancellationTokenNonePropertyGetter);
                        }

                        il.EmitCall(OpCodes.Callvirt, invokingMethod, null);

                        if (returnTypeIsValueTask)
                        {
                            // We must convert the Task or Task<T> returned from JsonRpc into a ValueTask or ValueTask<T>
                            il.Emit(OpCodes.Newobj, method.ReturnType.GetTypeInfo().GetConstructor(new Type[] { invokingMethod.ReturnType }));
                        }

                        il.Emit(OpCodes.Ret);
                    }

                    // The second argument is an array of arguments for the RPC method.
                    il.MarkLabel(positionalArgsLabel);
                    {
                        il.Emit(OpCodes.Ldc_I4, argumentCountExcludingCancellationToken);
                        il.Emit(OpCodes.Newarr, typeof(object));

                        for (int i = 0; i < argumentCountExcludingCancellationToken; i++)
                        {
                            il.Emit(OpCodes.Dup); // duplicate the array on the stack
                            il.Emit(OpCodes.Ldc_I4, i); // push the index of the array to be initialized.
                            il.Emit(OpCodes.Ldarg, i + 1); // push the associated argument
                            if (methodParameters[i].ParameterType.GetTypeInfo().IsValueType)
                            {
                                il.Emit(OpCodes.Box, methodParameters[i].ParameterType); // box if the argument is a value type
                            }

                            il.Emit(OpCodes.Stelem_Ref); // set the array element.
                        }

                        MethodInfo invokingMethod;
                        if (cancellationTokenParameter != null)
                        {
                            il.Emit(OpCodes.Ldarg, cancellationTokenParameter.Position + 1);
                            invokingMethod = hasReturnValue ? invokeWithCancellationAsyncOfTaskOfTMethodInfo : invokeWithCancellationAsyncOfTaskMethodInfo;
                        }
                        else
                        {
                            invokingMethod = hasReturnValue ? invokeAsyncOfTaskOfTMethodInfo : invokeAsyncOfTaskMethodInfo;
                        }

                        // Construct the InvokeAsync<T> method with the T argument supplied if we have a return type.
                        if (hasReturnValue)
                        {
                            invokingMethod = invokingMethod.MakeGenericMethod(method.ReturnType.GetTypeInfo().GenericTypeArguments[0]);
                        }

                        il.EmitCall(OpCodes.Callvirt, invokingMethod, null);

                        if (returnTypeIsValueTask)
                        {
                            // We must convert the Task or Task<T> returned from JsonRpc into a ValueTask or ValueTask<T>
                            il.Emit(OpCodes.Newobj, method.ReturnType.GetTypeInfo().GetConstructor(new Type[] { invokingMethod.ReturnType }));
                        }

                        il.Emit(OpCodes.Ret);
                    }

                    proxyTypeBuilder.DefineMethodOverride(methodBuilder, method);
                }

                generatedType = proxyTypeBuilder.CreateTypeInfo();
            }

            lock (GeneratedProxiesByInterface)
            {
                if (!GeneratedProxiesByInterface.TryGetValue(serviceInterface, out var raceGeneratedType))
                {
                    GeneratedProxiesByInterface.Add(serviceInterface, generatedType);
                }
                else
                {
                    // Ensure we only expose the same generated type externally.
                    generatedType = raceGeneratedType;
                }
            }

#if SaveAssembly
            AssemblyBuilder.Save(ProxyModuleBuilder.ScopeName);
            System.IO.File.Move(ProxyModuleBuilder.ScopeName, ProxyModuleBuilder.ScopeName + ".dll");
#endif

            return generatedType;
        }

        private static ConstructorInfo CreateParameterObjectType(ParameterInfo[] parameters, Type parentType)
        {
            Requires.NotNull(parameters, nameof(parameters));
            if (parameters.Length == 0)
            {
                return ObjectCtor;
            }

            var proxyTypeBuilder = ProxyModuleBuilder.DefineType(
                string.Format(CultureInfo.InvariantCulture, "_param_{0}", Guid.NewGuid()),
                TypeAttributes.NotPublic);

            var parameterTypes = new Type[parameters.Length];
            var fields = new FieldBuilder[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                parameterTypes[i] = parameters[i].ParameterType;
                fields[i] = proxyTypeBuilder.DefineField(parameters[i].Name, parameters[i].ParameterType, FieldAttributes.Public | FieldAttributes.InitOnly);
            }

            var ctor = proxyTypeBuilder.DefineConstructor(
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                CallingConventions.Standard,
                parameterTypes);
            for (int i = 0; i < parameters.Length; i++)
            {
                ctor.DefineParameter(i + 1, ParameterAttributes.In, parameters[i].Name);
            }

            var il = ctor.GetILGenerator();

            // : base()
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, ObjectCtor);

            for (int i = 0; i < parameters.Length; i++)
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg, i + 1);
                il.Emit(OpCodes.Stfld, fields[i]);
            }

            il.Emit(OpCodes.Ret);

            // Finalize the type
            proxyTypeBuilder.CreateTypeInfo();

            return ctor;
        }

        private static void ImplementRaiseEventMethod(ILGenerator il, FieldBuilder evtField, FieldBuilder jsonRpcField)
        {
            var retLabel = il.DefineLabel();
            var invokeLabel = il.DefineLabel();

            // var eventName = this.EventName;
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, evtField);

            // if (eventName != null) goto Invoke;
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Brtrue_S, invokeLabel);

            // Condition was false, so clear the stack and return.
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Br_S, retLabel);

            // Invoke:
            il.MarkLabel(invokeLabel);

            // eventName.Invoke(this.rpc, args);
            // we already have eventName as a pseudo-local on our stack.
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, jsonRpcField);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Callvirt, evtField.FieldType.GetTypeInfo().GetDeclaredMethod(nameof(EventHandler.Invoke)));

            il.MarkLabel(retLabel);
            il.Emit(OpCodes.Ret);
        }

        private static void ImplementEventAccessor(ILGenerator il, FieldInfo evtField, MethodInfo combineOrRemoveMethod)
        {
            var loopStart = il.DefineLabel();

            il.DeclareLocal(evtField.FieldType); // loc_0
            il.DeclareLocal(evtField.FieldType); // loc_1
            il.DeclareLocal(evtField.FieldType); // loc_2

            // EventHandler eventHandler = this.EventName;
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, evtField);
            il.Emit(OpCodes.Stloc_0);

            il.MarkLabel(loopStart);
            {
                // var eventHandler2 = eventHandler;
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Stloc_1);

                // EventHandler value2 = (EventHandler)Delegate.CombineOrRemove(eventHandler2, value);
                il.Emit(OpCodes.Ldloc_1);
                il.Emit(OpCodes.Ldarg_1);
                il.EmitCall(OpCodes.Call, combineOrRemoveMethod, EmptyTypes);
                il.Emit(OpCodes.Castclass, evtField.FieldType);
                il.Emit(OpCodes.Stloc_2);

                // eventHandler = Interlocked.CompareExchange<EventHandler>(ref this.SomethingChanged, value2, eventHandler2);
                var compareExchangeClosedGeneric = CompareExchangeMethod.MakeGenericMethod(evtField.FieldType);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldflda, evtField);
                il.Emit(OpCodes.Ldloc_2);
                il.Emit(OpCodes.Ldloc_1);
                il.EmitCall(OpCodes.Call, compareExchangeClosedGeneric, EmptyTypes);
                il.Emit(OpCodes.Stloc_0);

                // while ((object)eventHandler != eventHandler2);
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Ldloc_1);
                il.Emit(OpCodes.Bne_Un_S, loopStart);
            }

            il.Emit(OpCodes.Ret);
        }

        private static void VerifySupported(bool condition, string messageFormat, MemberInfo problematicMember, params object[] otherArgs)
        {
            if (!condition)
            {
                object[] formattingArgs = new object[1 + otherArgs?.Length ?? 0];
                formattingArgs[0] = problematicMember.DeclaringType.FullName + "." + problematicMember.Name;
                if (otherArgs?.Length > 0)
                {
                    Array.Copy(otherArgs, 0, formattingArgs, 1, otherArgs.Length);
                }

                throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, messageFormat, formattingArgs));
            }
        }

        private static IEnumerable<T> FindAllOnThisAndOtherInterfaces<T>(TypeInfo interfaceType, Func<TypeInfo, IEnumerable<T>> oneInterfaceQuery)
        {
            Requires.NotNull(interfaceType, nameof(interfaceType));
            Requires.NotNull(oneInterfaceQuery, nameof(oneInterfaceQuery));

            var result = oneInterfaceQuery(interfaceType);
            return result.Concat(interfaceType.ImplementedInterfaces.SelectMany(i => oneInterfaceQuery(i.GetTypeInfo())));
        }
    }
}
