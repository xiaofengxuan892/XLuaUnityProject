/*
 * Tencent is pleased to support the open source community by making xLua available.
 * Copyright (C) 2016 THL A29 Limited, a Tencent company. All rights reserved.
 * Licensed under the MIT License (the "License"); you may not use this file except in compliance with the License. You may obtain a copy of the License at
 * http://opensource.org/licenses/MIT
 * Unless required by applicable law or agreed to in writing, software distributed under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the License for the specific language governing permissions and limitations under the License.
*/

using System.Collections.Generic;
using System;
using System.Reflection;
using System.Linq;
using System.Runtime.CompilerServices;

#if USE_UNI_LUA
using LuaAPI = UniLua.Lua;
using RealStatePtr = UniLua.ILuaState;
using LuaCSFunction = UniLua.CSharpFunctionDelegate;
#else
using LuaAPI = XLua.LuaDLL.Lua;
using RealStatePtr = System.IntPtr;
using LuaCSFunction = XLua.LuaDLL.lua_CSFunction;
#endif

namespace XLua
{
	public enum LazyMemberTypes
	{
		Method,
		FieldGet,
		FieldSet,
		PropertyGet,
		PropertySet,
		Event,
	}

	public static partial class Utils
	{
		public static bool LoadField(RealStatePtr L, int idx, string field_name)
		{
			idx = idx > 0 ? idx : LuaAPI.lua_gettop(L) + idx + 1;// abs of index
			LuaAPI.xlua_pushasciistring(L, field_name);
			LuaAPI.lua_rawget(L, idx);
			return !LuaAPI.lua_isnil(L, -1);
		}

		public static RealStatePtr GetMainState(RealStatePtr L)
		{
			RealStatePtr ret = default(RealStatePtr);
			LuaAPI.xlua_pushasciistring(L, LuaEnv.MAIN_SHREAD);
			LuaAPI.lua_rawget(L, LuaIndexes.LUA_REGISTRYINDEX);
			if (LuaAPI.lua_isthread(L, -1))
			{
				ret = LuaAPI.lua_tothread(L, -1);
			}
			LuaAPI.lua_pop(L, 1);
			return ret;
		}

#if (UNITY_WSA && !ENABLE_IL2CPP) && !UNITY_EDITOR
        public static List<Assembly> _assemblies;
        public static List<Assembly> GetAssemblies()
        {
            if (_assemblies == null)
            {
                System.Threading.Tasks.Task t = new System.Threading.Tasks.Task(() =>
                {
                    _assemblies = GetAssemblyList().Result;
                });
                t.Start();
                t.Wait();
            }
            return _assemblies;

        }
        public static async System.Threading.Tasks.Task<List<Assembly>> GetAssemblyList()
        {
            List<Assembly> assemblies = new List<Assembly>();
            //return assemblies;
            var files = await Windows.ApplicationModel.Package.Current.InstalledLocation.GetFilesAsync();
            if (files == null)
                return assemblies;

            foreach (var file in files.Where(file => file.FileType == ".dll" || file.FileType == ".exe"))
            {
                try
                {
                    assemblies.Add(Assembly.Load(new AssemblyName(file.DisplayName)));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(ex.Message);
                }

            }
            return assemblies;
        }
        public static IEnumerable<Type> GetAllTypes(bool exclude_generic_definition = true)
        {
            var assemblies = GetAssemblies();
            return from assembly in assemblies
                   where !(assembly.IsDynamic)
                   from type in assembly.GetTypes()
                   where exclude_generic_definition ? !type.GetTypeInfo().IsGenericTypeDefinition : true
                   select type;
        }
#else
		public static List<Type> GetAllTypes(bool exclude_generic_definition = true)
		{
			List<Type> allTypes = new List<Type>();
			var assemblies = AppDomain.CurrentDomain.GetAssemblies();
			for (int i = 0; i < assemblies.Length; i++)
			{
				try
				{
#if (UNITY_EDITOR || XLUA_GENERAL) && !NET_STANDARD_2_0
					if (!(assemblies[i].ManifestModule is System.Reflection.Emit.ModuleBuilder))
					{
#endif
						allTypes.AddRange(assemblies[i].GetTypes()
						.Where(type => exclude_generic_definition ? !type.IsGenericTypeDefinition() : true)
						);
#if (UNITY_EDITOR || XLUA_GENERAL) && !NET_STANDARD_2_0
					}
#endif
				}
				catch (Exception)
				{
				}
			}

			return allTypes;
		}
#endif

		static LuaCSFunction genFieldGetter(Type type, FieldInfo field)
		{
			if (field.IsStatic)
			{
				return (RealStatePtr L) =>
				{
					ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
					translator.PushAny(L, field.GetValue(null));
					return 1;
				};
			}
			else
			{
				return (RealStatePtr L) =>
				{
					ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
					object obj = translator.FastGetCSObj(L, 1);
					if (obj == null || !type.IsInstanceOfType(obj))
					{
						return LuaAPI.luaL_error(L, "Expected type " + type + ", but got " + (obj == null ? "null" : obj.GetType().ToString()) + ", while get field " + field);
					}

					translator.PushAny(L, field.GetValue(obj));
					return 1;
				};
			}
		}

		static LuaCSFunction genFieldSetter(Type type, FieldInfo field)
		{
			if (field.IsStatic)
			{
				return (RealStatePtr L) =>
				{
					ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
					object val = translator.GetObject(L, 1, field.FieldType);
					if (field.FieldType.IsValueType() && Nullable.GetUnderlyingType(field.FieldType) == null && val == null)
					{
						return LuaAPI.luaL_error(L, type.Name + "." + field.Name + " Expected type " + field.FieldType);
					}
					field.SetValue(null, val);
					return 0;
				};
			}
			else
			{
				return (RealStatePtr L) =>
				{
					ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);

					object obj = translator.FastGetCSObj(L, 1);
					if (obj == null || !type.IsInstanceOfType(obj))
					{
						return LuaAPI.luaL_error(L, "Expected type " + type + ", but got " + (obj == null ? "null" : obj.GetType().ToString()) + ", while set field " + field);
					}

					object val = translator.GetObject(L, 2, field.FieldType);
					if (field.FieldType.IsValueType() && Nullable.GetUnderlyingType(field.FieldType) == null && val == null)
					{
						return LuaAPI.luaL_error(L, type.Name + "." + field.Name + " Expected type " + field.FieldType);
					}
					field.SetValue(obj, val);
					if (type.IsValueType())
					{
						translator.Update(L, 1, obj);
					}
					return 0;
				};
			}
		}

		static LuaCSFunction genItemGetter(Type type, PropertyInfo[] props)
		{
			props = props.Where(prop => !prop.GetIndexParameters()[0].ParameterType.IsAssignableFrom(typeof(string))).ToArray();
			if (props.Length == 0)
			{
				return null;
			}
			Type[] params_type = new Type[props.Length];
			for (int i = 0; i < props.Length; i++)
			{
				params_type[i] = props[i].GetIndexParameters()[0].ParameterType;
			}
			object[] arg = new object[1] { null };
			return (RealStatePtr L) =>
			{
				ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
				object obj = translator.FastGetCSObj(L, 1);
				if (obj == null || !type.IsInstanceOfType(obj))
				{
					return LuaAPI.luaL_error(L, "Expected type " + type + ", but got " + (obj == null ? "null" : obj.GetType().ToString()) + ", while get prop " + props[0].Name);
				}

				for (int i = 0; i < props.Length; i++)
				{
					if (!translator.Assignable(L, 2, params_type[i]))
					{
						continue;
					}
					else
					{
						PropertyInfo prop = props[i];
						try
						{
							object index = translator.GetObject(L, 2, params_type[i]);
							arg[0] = index;
							object ret = prop.GetValue(obj, arg);
							LuaAPI.lua_pushboolean(L, true);
							translator.PushAny(L, ret);
							return 2;
						}
						catch (Exception e)
						{
							return LuaAPI.luaL_error(L, "try to get " + type + "." + prop.Name + " throw a exception:" + e + ",stack:" + e.StackTrace);
						}
					}
				}

				LuaAPI.lua_pushboolean(L, false);
				return 1;
			};
		}

		static LuaCSFunction genItemSetter(Type type, PropertyInfo[] props)
		{
			props = props.Where(prop => !prop.GetIndexParameters()[0].ParameterType.IsAssignableFrom(typeof(string))).ToArray();
			if (props.Length == 0)
			{
				return null;
			}
			Type[] params_type = new Type[props.Length];
			for (int i = 0; i < props.Length; i++)
			{
				params_type[i] = props[i].GetIndexParameters()[0].ParameterType;
			}
			object[] arg = new object[1] { null };
			return (RealStatePtr L) =>
			{
				ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
				object obj = translator.FastGetCSObj(L, 1);
				if (obj == null || !type.IsInstanceOfType(obj))
				{
					return LuaAPI.luaL_error(L, "Expected type " + type + ", but got " + (obj == null ? "null" : obj.GetType().ToString()) + ", while set prop " + props[0].Name);
				}

				for (int i = 0; i < props.Length; i++)
				{
					if (!translator.Assignable(L, 2, params_type[i]))
					{
						continue;
					}
					else
					{
						PropertyInfo prop = props[i];
						try
						{
							arg[0] = translator.GetObject(L, 2, params_type[i]);
							object val = translator.GetObject(L, 3, prop.PropertyType);
							if (val == null)
							{
								return LuaAPI.luaL_error(L, type.Name + "." + prop.Name + " Expected type " + prop.PropertyType);
							}
							prop.SetValue(obj, val, arg);
							LuaAPI.lua_pushboolean(L, true);

							return 1;
						}
						catch (Exception e)
						{
							return LuaAPI.luaL_error(L, "try to set " + type + "." + prop.Name + " throw a exception:" + e + ",stack:" + e.StackTrace);
						}
					}
				}

				LuaAPI.lua_pushboolean(L, false);
				return 1;
			};
		}

		static LuaCSFunction genEnumCastFrom(Type type)
		{
			return (RealStatePtr L) =>
			{
				try
				{
					ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
					return translator.TranslateToEnumToTop(L, type, 1);
				}
				catch (Exception e)
				{
					return LuaAPI.luaL_error(L, "cast to " + type + " exception:" + e);
				}
			};
		}

		internal static IEnumerable<MethodInfo> GetExtensionMethodsOf(Type type_to_be_extend)
		{
			if (InternalGlobals.extensionMethodMap == null)
			{
				List<Type> type_def_extention_method = new List<Type>();

				IEnumerator<Type> enumerator = GetAllTypes().GetEnumerator();

				while (enumerator.MoveNext())
				{
					Type type = enumerator.Current;
					if (type.IsDefined(typeof(ExtensionAttribute), false) && (
							type.IsDefined(typeof(ReflectionUseAttribute), false)
#if UNITY_EDITOR || XLUA_GENERAL
							|| type.IsDefined(typeof(LuaCallCSharpAttribute), false)
#endif
						))
					{
						type_def_extention_method.Add(type);
					}

					if (!type.IsAbstract() || !type.IsSealed()) continue;

					var fields = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
					for (int i = 0; i < fields.Length; i++)
					{
						var field = fields[i];
						if ((field.IsDefined(typeof(ReflectionUseAttribute), false)
#if UNITY_EDITOR || XLUA_GENERAL
							|| field.IsDefined(typeof(LuaCallCSharpAttribute), false)
#endif
							) && (typeof(IEnumerable<Type>)).IsAssignableFrom(field.FieldType))
						{
							type_def_extention_method.AddRange((field.GetValue(null) as IEnumerable<Type>)
								.Where(t => t.IsDefined(typeof(ExtensionAttribute), false)));
						}
					}

					var props = type.GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
					for (int i = 0; i < props.Length; i++)
					{
						var prop = props[i];
						if ((prop.IsDefined(typeof(ReflectionUseAttribute), false)
#if UNITY_EDITOR || XLUA_GENERAL
							|| prop.IsDefined(typeof(LuaCallCSharpAttribute), false)
#endif
							) && (typeof(IEnumerable<Type>)).IsAssignableFrom(prop.PropertyType))
						{
							type_def_extention_method.AddRange((prop.GetValue(null, null) as IEnumerable<Type>)
								.Where(t => t.IsDefined(typeof(ExtensionAttribute), false)));
						}
					}
				}
				enumerator.Dispose();

				InternalGlobals.extensionMethodMap = (from type in type_def_extention_method
													  from method in type.GetMethods(BindingFlags.Static | BindingFlags.Public)
													  where method.IsDefined(typeof(ExtensionAttribute), false) && IsSupportedMethod(method)
													  group method by getExtendedType(method)).ToDictionary(g => g.Key, g => g as IEnumerable<MethodInfo>);
			}
			IEnumerable<MethodInfo> ret = null;
			InternalGlobals.extensionMethodMap.TryGetValue(type_to_be_extend, out ret);
			return ret;
		}

		struct MethodKey
		{
			public string Name;
			public bool IsStatic;
		}

		static void makeReflectionWrap(RealStatePtr L, Type type, int cls_field, int cls_getter, int cls_setter,
			int obj_field, int obj_getter, int obj_setter, int obj_meta, out LuaCSFunction item_getter, out LuaCSFunction item_setter, BindingFlags access)
		{
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			BindingFlags flag = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | access;
			FieldInfo[] fields = type.GetFields(flag);
			EventInfo[] all_events = type.GetEvents(flag | BindingFlags.Public | BindingFlags.NonPublic);

            LuaAPI.lua_checkstack(L, 2);

            for (int i = 0; i < fields.Length; ++i)
			{
				FieldInfo field = fields[i];
				string fieldName = field.Name;
				// skip hotfix inject field
				if (field.IsStatic && (field.Name.StartsWith("__Hotfix") || field.Name.StartsWith("_c__Hotfix")) && typeof(Delegate).IsAssignableFrom(field.FieldType))
				{
					continue;
				}
				if (all_events.Any(e => e.Name == fieldName))
				{
					fieldName = "&" + fieldName;
				}

				if (field.IsStatic && (field.IsInitOnly || field.IsLiteral))
				{
					LuaAPI.xlua_pushasciistring(L, fieldName);
					translator.PushAny(L, field.GetValue(null));
					LuaAPI.lua_rawset(L, cls_field);
				}
				else
				{
					LuaAPI.xlua_pushasciistring(L, fieldName);
					translator.PushFixCSFunction(L, genFieldGetter(type, field));
					LuaAPI.lua_rawset(L, field.IsStatic ? cls_getter : obj_getter);

					LuaAPI.xlua_pushasciistring(L, fieldName);
					translator.PushFixCSFunction(L, genFieldSetter(type, field));
					LuaAPI.lua_rawset(L, field.IsStatic ? cls_setter : obj_setter);
				}
			}

			EventInfo[] events = type.GetEvents(flag);
			for (int i = 0; i < events.Length; ++i)
			{
				EventInfo eventInfo = events[i];
				LuaAPI.xlua_pushasciistring(L, eventInfo.Name);
				translator.PushFixCSFunction(L, translator.methodWrapsCache.GetEventWrap(type, eventInfo.Name));
				bool is_static = (eventInfo.GetAddMethod(true) != null) ? eventInfo.GetAddMethod(true).IsStatic : eventInfo.GetRemoveMethod(true).IsStatic;
				LuaAPI.lua_rawset(L, is_static ? cls_field : obj_field);
			}

			List<PropertyInfo> items = new List<PropertyInfo>();
			PropertyInfo[] props = type.GetProperties(flag);
			for (int i = 0; i < props.Length; ++i)
			{
				PropertyInfo prop = props[i];
				if (prop.GetIndexParameters().Length > 0)
				{
					items.Add(prop);
				}
			}

			var item_array = items.ToArray();
			item_getter = item_array.Length > 0 ? genItemGetter(type, item_array) : null;
			item_setter = item_array.Length > 0 ? genItemSetter(type, item_array) : null;
			MethodInfo[] methods = type.GetMethods(flag);
			if (access == BindingFlags.NonPublic)
			{
				methods = type.GetMethods(flag | BindingFlags.Public).Join(methods, p => p.Name, q => q.Name, (p, q) => p).ToArray();
			}
			Dictionary<MethodKey, List<MemberInfo>> pending_methods = new Dictionary<MethodKey, List<MemberInfo>>();
			for (int i = 0; i < methods.Length; ++i)
			{
				MethodInfo method = methods[i];
				string method_name = method.Name;

				MethodKey method_key = new MethodKey { Name = method_name, IsStatic = method.IsStatic };
				List<MemberInfo> overloads;
				if (pending_methods.TryGetValue(method_key, out overloads))
				{
					overloads.Add(method);
					continue;
				}

				//indexer
				if (method.IsSpecialName && ((method.Name == "get_Item" && method.GetParameters().Length == 1) || (method.Name == "set_Item" && method.GetParameters().Length == 2)))
				{
					if (!method.GetParameters()[0].ParameterType.IsAssignableFrom(typeof(string)))
					{
						continue;
					}
				}

				if ((method_name.StartsWith("add_") || method_name.StartsWith("remove_")) && method.IsSpecialName)
				{
					continue;
				}

				if (method_name.StartsWith("op_") && method.IsSpecialName) // 操作符
				{
					if (InternalGlobals.supportOp.ContainsKey(method_name))
					{
						if (overloads == null)
						{
							overloads = new List<MemberInfo>();
							pending_methods.Add(method_key, overloads);
						}
						overloads.Add(method);
					}
					continue;
				}
				else if (method_name.StartsWith("get_") && method.IsSpecialName && method.GetParameters().Length != 1) // getter of property
				{
					string prop_name = method.Name.Substring(4);
					LuaAPI.xlua_pushasciistring(L, prop_name);
					translator.PushFixCSFunction(L, translator.methodWrapsCache._GenMethodWrap(method.DeclaringType, prop_name, new MethodBase[] { method }).Call);
					LuaAPI.lua_rawset(L, method.IsStatic ? cls_getter : obj_getter);
				}
				else if (method_name.StartsWith("set_") && method.IsSpecialName && method.GetParameters().Length != 2) // setter of property
				{
					string prop_name = method.Name.Substring(4);
					LuaAPI.xlua_pushasciistring(L, prop_name);
					translator.PushFixCSFunction(L, translator.methodWrapsCache._GenMethodWrap(method.DeclaringType, prop_name, new MethodBase[] { method }).Call);
					LuaAPI.lua_rawset(L, method.IsStatic ? cls_setter : obj_setter);
				}
				else if (method_name == ".ctor" && method.IsConstructor)
				{
					continue;
				}
				else
				{
					if (overloads == null)
					{
						overloads = new List<MemberInfo>();
						pending_methods.Add(method_key, overloads);
					}
					overloads.Add(method);
				}
			}


			IEnumerable<MethodInfo> extend_methods = GetExtensionMethodsOf(type);
			if (extend_methods != null)
			{
				foreach (var extend_method in extend_methods)
				{
					MethodKey method_key = new MethodKey { Name = extend_method.Name, IsStatic = false };
					List<MemberInfo> overloads;
					if (pending_methods.TryGetValue(method_key, out overloads))
					{
						overloads.Add(extend_method);
						continue;
					}
					else
					{
						overloads = new List<MemberInfo>() { extend_method };
						pending_methods.Add(method_key, overloads);
					}
				}
			}

			foreach (var kv in pending_methods)
			{
				if (kv.Key.Name.StartsWith("op_")) // 操作符
				{
					LuaAPI.xlua_pushasciistring(L, InternalGlobals.supportOp[kv.Key.Name]);
					translator.PushFixCSFunction(L,
						new LuaCSFunction(translator.methodWrapsCache._GenMethodWrap(type, kv.Key.Name, kv.Value.ToArray()).Call));
					LuaAPI.lua_rawset(L, obj_meta);
				}
				else
				{
					LuaAPI.xlua_pushasciistring(L, kv.Key.Name);
					translator.PushFixCSFunction(L,
						new LuaCSFunction(translator.methodWrapsCache._GenMethodWrap(type, kv.Key.Name, kv.Value.ToArray()).Call));
					LuaAPI.lua_rawset(L, kv.Key.IsStatic ? cls_field : obj_field);
				}
			}
		}

		public static void loadUpvalue(RealStatePtr L, Type type, string metafunc, int index)
		{
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			LuaAPI.xlua_pushasciistring(L, metafunc);
			LuaAPI.lua_rawget(L, LuaIndexes.LUA_REGISTRYINDEX);
			translator.Push(L, type);
			LuaAPI.lua_rawget(L, -2);
			LuaAPI.lua_remove(L, -2);
			for (int i = 1; i <= index; i++)
			{
				LuaAPI.lua_getupvalue(L, -i, i);
				if (LuaAPI.lua_isnil(L, -1))
				{
					LuaAPI.lua_pop(L, 1);
					LuaAPI.lua_newtable(L);
					LuaAPI.lua_pushvalue(L, -1);
					LuaAPI.lua_setupvalue(L, -i - 2, i);
				}
			}
			for (int i = 0; i < index; i++)
			{
				LuaAPI.lua_remove(L, -2);
			}
		}

        public static void RegisterEnumType(RealStatePtr L, Type type)
        {
            ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            foreach (var name in Enum.GetNames(type))
            {
                RegisterObject(L, translator, Utils.CLS_IDX, name, Enum.Parse(type, name));
            }
        }


        public static void MakePrivateAccessible(RealStatePtr L, Type type)
		{
            LuaAPI.lua_checkstack(L, 20);

            int oldTop = LuaAPI.lua_gettop(L);

			LuaAPI.luaL_getmetatable(L, type.FullName);
			if (LuaAPI.lua_isnil(L, -1))
			{
				LuaAPI.lua_settop(L, oldTop);
				throw new Exception("can not find the metatable for " + type);
			}
			int obj_meta = LuaAPI.lua_gettop(L);

			LoadCSTable(L, type);
			if (LuaAPI.lua_isnil(L, -1))
			{
				LuaAPI.lua_settop(L, oldTop);
				throw new Exception("can not find the class for " + type);
			}
			int cls_field = LuaAPI.lua_gettop(L);

			loadUpvalue(L, type, LuaIndexsFieldName, 2);
			int obj_getter = LuaAPI.lua_gettop(L);
			loadUpvalue(L, type, LuaIndexsFieldName, 1);
			int obj_field = LuaAPI.lua_gettop(L);

			loadUpvalue(L, type, LuaNewIndexsFieldName, 1);
			int obj_setter = LuaAPI.lua_gettop(L);

			loadUpvalue(L, type, LuaClassIndexsFieldName, 1);
			int cls_getter = LuaAPI.lua_gettop(L);

			loadUpvalue(L, type, LuaClassNewIndexsFieldName, 1);
			int cls_setter = LuaAPI.lua_gettop(L);

			LuaCSFunction item_getter;
			LuaCSFunction item_setter;
			makeReflectionWrap(L, type, cls_field, cls_getter, cls_setter, obj_field, obj_getter, obj_setter, obj_meta,
				out item_getter, out item_setter, BindingFlags.NonPublic);
			LuaAPI.lua_settop(L, oldTop);

			foreach (var nested_type in type.GetNestedTypes(BindingFlags.NonPublic))
			{
				if ((!nested_type.IsAbstract() && typeof(Delegate).IsAssignableFrom(nested_type))
					|| nested_type.IsGenericTypeDefinition())
				{
					continue;
				}
				ObjectTranslatorPool.Instance.Find(L).TryDelayWrapLoader(L, nested_type);
				MakePrivateAccessible(L, nested_type);
			}
		}

		[MonoPInvokeCallback(typeof(LuaCSFunction))]
		internal static int LazyReflectionCall(RealStatePtr L)
		{
			try
			{
				ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
				Type type;
				translator.Get(L, LuaAPI.xlua_upvalueindex(1), out type);
				LazyMemberTypes memberType = (LazyMemberTypes)LuaAPI.xlua_tointeger(L, LuaAPI.xlua_upvalueindex(2));
				string memberName = LuaAPI.lua_tostring(L, LuaAPI.xlua_upvalueindex(3));
				bool isStatic = LuaAPI.lua_toboolean(L, LuaAPI.xlua_upvalueindex(4));
				LuaCSFunction wrap = null;
				//UnityEngine.Debug.Log(">>>>> " + type + " " + memberName);

				switch (memberType)
				{
					case LazyMemberTypes.Method:
						var members = type.GetMember(memberName);
						if (members == null || members.Length == 0)
						{
							return LuaAPI.luaL_error(L, "can not find " + memberName + " for " + type);
						}
						IEnumerable<MemberInfo> methods = members;
						if (!isStatic)
						{
							var extensionMethods = GetExtensionMethodsOf(type);
							if (extensionMethods != null)
							{
								methods = methods.Concat(extensionMethods.Where(m => m.Name == memberName).Cast<MemberInfo>());
							}
						}
						wrap = new LuaCSFunction(translator.methodWrapsCache._GenMethodWrap(type, memberName, methods.ToArray()).Call);
						if (isStatic)
						{
							LoadCSTable(L, type);
						}
						else
						{
							loadUpvalue(L, type, LuaIndexsFieldName, 1);
						}
						if (LuaAPI.lua_isnil(L, -1))
						{
							return LuaAPI.luaL_error(L, "can not find the meta info for " + type);
						}
						break;
					case LazyMemberTypes.FieldGet:
					case LazyMemberTypes.FieldSet:
						var field = type.GetField(memberName);
						if (field == null)
						{
							return LuaAPI.luaL_error(L, "can not find " + memberName + " for " + type);
						}
						if (isStatic)
						{
							if (memberType == LazyMemberTypes.FieldGet)
							{
								loadUpvalue(L, type, LuaClassIndexsFieldName, 1);
							}
							else
							{
								loadUpvalue(L, type, LuaClassNewIndexsFieldName, 1);
							}
						}
						else
						{
							if (memberType == LazyMemberTypes.FieldGet)
							{
								loadUpvalue(L, type, LuaIndexsFieldName, 2);
							}
							else
							{
								loadUpvalue(L, type, LuaNewIndexsFieldName, 1);
							}
						}

						wrap = (memberType == LazyMemberTypes.FieldGet) ? genFieldGetter(type, field) : genFieldSetter(type, field);

						break;
					case LazyMemberTypes.PropertyGet:
					case LazyMemberTypes.PropertySet:
						var prop = type.GetProperty(memberName);
						if (prop == null)
						{
							return LuaAPI.luaL_error(L, "can not find " + memberName + " for " + type);
						}
						if (isStatic)
						{
							if (memberType == LazyMemberTypes.PropertyGet)
							{
								loadUpvalue(L, type, LuaClassIndexsFieldName, 1);
							}
							else
							{
								loadUpvalue(L, type, LuaClassNewIndexsFieldName, 1);
							}
						}
						else
						{
							if (memberType == LazyMemberTypes.PropertyGet)
							{
								loadUpvalue(L, type, LuaIndexsFieldName, 2);
							}
							else
							{
								loadUpvalue(L, type, LuaNewIndexsFieldName, 1);
							}
						}

						if (LuaAPI.lua_isnil(L, -1))
						{
							return LuaAPI.luaL_error(L, "can not find the meta info for " + type);
						}

						wrap = translator.methodWrapsCache._GenMethodWrap(prop.DeclaringType, prop.Name, new MethodBase[] { (memberType == LazyMemberTypes.PropertyGet) ? prop.GetGetMethod() : prop.GetSetMethod() }).Call;
						break;
					case LazyMemberTypes.Event:
						var eventInfo = type.GetEvent(memberName);
						if (eventInfo == null)
						{
							return LuaAPI.luaL_error(L, "can not find " + memberName + " for " + type);
						}
						if (isStatic)
						{
							LoadCSTable(L, type);
						}
						else
						{
							loadUpvalue(L, type, LuaIndexsFieldName, 1);
						}
						if (LuaAPI.lua_isnil(L, -1))
						{
							return LuaAPI.luaL_error(L, "can not find the meta info for " + type);
						}
						wrap = translator.methodWrapsCache.GetEventWrap(type, eventInfo.Name);
						break;
					default:
						return LuaAPI.luaL_error(L, "unsupport member type" + memberType);
				}

				LuaAPI.xlua_pushasciistring(L, memberName);
				translator.PushFixCSFunction(L, wrap);
				LuaAPI.lua_rawset(L, -3);
				LuaAPI.lua_pop(L, 1);
				return wrap(L);
			}
			catch (Exception e)
			{
				return LuaAPI.luaL_error(L, "c# exception in LazyReflectionCall:" + e);
			}
		}

		public static void ReflectionWrap(RealStatePtr L, Type type, bool privateAccessible)
		{
            LuaAPI.lua_checkstack(L, 20);

            int top_enter = LuaAPI.lua_gettop(L);
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			//获取注册表中该type对应的数值，即一个普通的table，但由于该table的特殊用途，这里当作“元表”来处理
			LuaAPI.luaL_getmetatable(L, type.FullName);  //注意：即使没有，也会压nil入栈
			if (LuaAPI.lua_isnil(L, -1))
			{
				LuaAPI.lua_pop(L, 1);
				LuaAPI.luaL_newmetatable(L, type.FullName);
			}

			//执行完“luaL_newmetatable”后此时栈顶为新创建的空表“typeMetatable”
			LuaAPI.lua_pushlightuserdata(L, LuaAPI.xlua_tag());
			LuaAPI.lua_pushnumber(L, 1);
			//在"typeMetatable"中设置键值对：typeMetatable[xlua_tag] = 1，并将key，value出栈
			LuaAPI.lua_rawset(L, -3);
			int obj_meta = LuaAPI.lua_gettop(L);  //由于key,value已出栈，故当前栈顶为“typeMetatable”

			LuaAPI.lua_newtable(L);  //创建一个空table，并将该table压入栈
			int cls_meta = LuaAPI.lua_gettop(L);  //获取该table的索引

			//为obj的“field, getter, setter”分别设置table，并获取该table在栈L中的索引
			LuaAPI.lua_newtable(L);
			int obj_field = LuaAPI.lua_gettop(L);
			LuaAPI.lua_newtable(L);
			int obj_getter = LuaAPI.lua_gettop(L);
			LuaAPI.lua_newtable(L);
			int obj_setter = LuaAPI.lua_gettop(L);

			LuaAPI.lua_newtable(L);
			int cls_field = LuaAPI.lua_gettop(L);

            //set cls_field to namespace
            SetCSTable(L, type, cls_field);
            //finish set cls_field to namespace
            LuaAPI.lua_newtable(L);
			int cls_getter = LuaAPI.lua_gettop(L);
			LuaAPI.lua_newtable(L);
			int cls_setter = LuaAPI.lua_gettop(L);

            LuaCSFunction item_getter;
			LuaCSFunction item_setter;
			makeReflectionWrap(L, type, cls_field, cls_getter, cls_setter, obj_field, obj_getter, obj_setter, obj_meta,
				out item_getter, out item_setter, privateAccessible ? (BindingFlags.Public | BindingFlags.NonPublic) : BindingFlags.Public);

			// init obj metatable
			LuaAPI.xlua_pushasciistring(L, "__gc");
			LuaAPI.lua_pushstdcallcfunction(L, translator.metaFunctions.GcMeta);
			LuaAPI.lua_rawset(L, obj_meta);

			LuaAPI.xlua_pushasciistring(L, "__tostring");
			LuaAPI.lua_pushstdcallcfunction(L, translator.metaFunctions.ToStringMeta);
			LuaAPI.lua_rawset(L, obj_meta);

			LuaAPI.xlua_pushasciistring(L, "__index");
			LuaAPI.lua_pushvalue(L, obj_field);
			LuaAPI.lua_pushvalue(L, obj_getter);
			translator.PushFixCSFunction(L, item_getter);
			translator.PushAny(L, type.BaseType());
			LuaAPI.xlua_pushasciistring(L, LuaIndexsFieldName);
			LuaAPI.lua_rawget(L, LuaIndexes.LUA_REGISTRYINDEX);
			LuaAPI.lua_pushnil(L);
			LuaAPI.gen_obj_indexer(L);
			//store in lua indexs function tables
			LuaAPI.xlua_pushasciistring(L, LuaIndexsFieldName);
			LuaAPI.lua_rawget(L, LuaIndexes.LUA_REGISTRYINDEX);
			translator.Push(L, type);
			LuaAPI.lua_pushvalue(L, -3);
			LuaAPI.lua_rawset(L, -3);
			LuaAPI.lua_pop(L, 1);
			LuaAPI.lua_rawset(L, obj_meta); // set __index

			LuaAPI.xlua_pushasciistring(L, "__newindex");
			LuaAPI.lua_pushvalue(L, obj_setter);
			translator.PushFixCSFunction(L, item_setter);
			translator.Push(L, type.BaseType());
			LuaAPI.xlua_pushasciistring(L, LuaNewIndexsFieldName);
			LuaAPI.lua_rawget(L, LuaIndexes.LUA_REGISTRYINDEX);
			LuaAPI.lua_pushnil(L);
			LuaAPI.gen_obj_newindexer(L);
			//store in lua newindexs function tables
			LuaAPI.xlua_pushasciistring(L, LuaNewIndexsFieldName);
			LuaAPI.lua_rawget(L, LuaIndexes.LUA_REGISTRYINDEX);
			translator.Push(L, type);
			LuaAPI.lua_pushvalue(L, -3);
			LuaAPI.lua_rawset(L, -3);
			LuaAPI.lua_pop(L, 1);
			LuaAPI.lua_rawset(L, obj_meta); // set __newindex
											//finish init obj metatable

			LuaAPI.xlua_pushasciistring(L, "UnderlyingSystemType");
			translator.PushAny(L, type);
			LuaAPI.lua_rawset(L, cls_field);

			if (type != null && type.IsEnum())
			{
				LuaAPI.xlua_pushasciistring(L, "__CastFrom");
				translator.PushFixCSFunction(L, genEnumCastFrom(type));
				LuaAPI.lua_rawset(L, cls_field);
			}

			//init class meta
			LuaAPI.xlua_pushasciistring(L, "__index");
			LuaAPI.lua_pushvalue(L, cls_getter);
			LuaAPI.lua_pushvalue(L, cls_field);
			translator.Push(L, type.BaseType());
			LuaAPI.xlua_pushasciistring(L, LuaClassIndexsFieldName);
			LuaAPI.lua_rawget(L, LuaIndexes.LUA_REGISTRYINDEX);
			LuaAPI.gen_cls_indexer(L);
			//store in lua indexs function tables
			LuaAPI.xlua_pushasciistring(L, LuaClassIndexsFieldName);
			LuaAPI.lua_rawget(L, LuaIndexes.LUA_REGISTRYINDEX);
			translator.Push(L, type);
			LuaAPI.lua_pushvalue(L, -3);
			LuaAPI.lua_rawset(L, -3);
			LuaAPI.lua_pop(L, 1);
			LuaAPI.lua_rawset(L, cls_meta); // set __index

			LuaAPI.xlua_pushasciistring(L, "__newindex");
			LuaAPI.lua_pushvalue(L, cls_setter);
			translator.Push(L, type.BaseType());
			LuaAPI.xlua_pushasciistring(L, LuaClassNewIndexsFieldName);
			LuaAPI.lua_rawget(L, LuaIndexes.LUA_REGISTRYINDEX);
			LuaAPI.gen_cls_newindexer(L);
			//store in lua newindexs function tables
			LuaAPI.xlua_pushasciistring(L, LuaClassNewIndexsFieldName);
			LuaAPI.lua_rawget(L, LuaIndexes.LUA_REGISTRYINDEX);
			translator.Push(L, type);
			LuaAPI.lua_pushvalue(L, -3);
			LuaAPI.lua_rawset(L, -3);
			LuaAPI.lua_pop(L, 1);
			LuaAPI.lua_rawset(L, cls_meta); // set __newindex

			LuaCSFunction constructor = typeof(Delegate).IsAssignableFrom(type) ? translator.metaFunctions.DelegateCtor : translator.methodWrapsCache.GetConstructorWrap(type);
			if (constructor == null)
			{
				constructor = (RealStatePtr LL) =>
				{
					return LuaAPI.luaL_error(LL, "No constructor for " + type);
				};
			}

			LuaAPI.xlua_pushasciistring(L, "__call");
			translator.PushFixCSFunction(L, constructor);
			LuaAPI.lua_rawset(L, cls_meta);

			LuaAPI.lua_pushvalue(L, cls_meta);
			LuaAPI.lua_setmetatable(L, cls_field);

			LuaAPI.lua_pop(L, 8);

			System.Diagnostics.Debug.Assert(top_enter == LuaAPI.lua_gettop(L));
		}

		//meta: -4, method:-3, getter: -2, setter: -1
		public static void BeginObjectRegister(Type type, RealStatePtr L, ObjectTranslator translator, int meta_count, int method_count, int getter_count,
			int setter_count, int type_id = -1)
		{
			if (type == null)
			{
				if (type_id == -1) throw new Exception("Fatal: must provide a type of type_id");
				LuaAPI.xlua_rawgeti(L, LuaIndexes.LUA_REGISTRYINDEX, type_id);
			}
			else
			{
				LuaAPI.luaL_getmetatable(L, type.FullName);
				if (LuaAPI.lua_isnil(L, -1))
				{
					LuaAPI.lua_pop(L, 1);
					LuaAPI.luaL_newmetatable(L, type.FullName);
				}
			}
			LuaAPI.lua_pushlightuserdata(L, LuaAPI.xlua_tag());
			LuaAPI.lua_pushnumber(L, 1);
			LuaAPI.lua_rawset(L, -3);

			if ((type == null || !translator.HasCustomOp(type)) && type != typeof(decimal))
			{
				LuaAPI.xlua_pushasciistring(L, "__gc");
				LuaAPI.lua_pushstdcallcfunction(L, translator.metaFunctions.GcMeta);
				LuaAPI.lua_rawset(L, -3);
			}

			LuaAPI.xlua_pushasciistring(L, "__tostring");
			LuaAPI.lua_pushstdcallcfunction(L, translator.metaFunctions.ToStringMeta);
			LuaAPI.lua_rawset(L, -3);

			if (method_count == 0)
			{
				LuaAPI.lua_pushnil(L);
			}
			else
			{
				LuaAPI.lua_createtable(L, 0, method_count);
			}

			if (getter_count == 0)
			{
				LuaAPI.lua_pushnil(L);
			}
			else
			{
				LuaAPI.lua_createtable(L, 0, getter_count);
			}

			if (setter_count == 0)
			{
				LuaAPI.lua_pushnil(L);
			}
			else
			{
				LuaAPI.lua_createtable(L, 0, setter_count);
			}
		}

		static int abs_idx(int top, int idx)
		{
			return idx > 0 ? idx : top + idx + 1;
		}

		public const int OBJ_META_IDX = -4;
		public const int METHOD_IDX = -3;
		public const int GETTER_IDX = -2;
		public const int SETTER_IDX = -1;

#if GEN_CODE_MINIMIZE
        public static void EndObjectRegister(Type type, RealStatePtr L, ObjectTranslator translator, CSharpWrapper csIndexer,
            CSharpWrapper csNewIndexer, Type base_type, CSharpWrapper arrayIndexer, CSharpWrapper arrayNewIndexer)
#else
		public static void EndObjectRegister(Type type, RealStatePtr L, ObjectTranslator translator, LuaCSFunction csIndexer,
			LuaCSFunction csNewIndexer, Type base_type, LuaCSFunction arrayIndexer, LuaCSFunction arrayNewIndexer)
#endif
		{
			int top = LuaAPI.lua_gettop(L);
			int meta_idx = abs_idx(top, OBJ_META_IDX);
			int method_idx = abs_idx(top, METHOD_IDX);
			int getter_idx = abs_idx(top, GETTER_IDX);
			int setter_idx = abs_idx(top, SETTER_IDX);

			//begin index gen
			LuaAPI.xlua_pushasciistring(L, "__index");
			LuaAPI.lua_pushvalue(L, method_idx);
			LuaAPI.lua_pushvalue(L, getter_idx);

			if (csIndexer == null)
			{
				LuaAPI.lua_pushnil(L);
			}
			else
			{
#if GEN_CODE_MINIMIZE
                translator.PushCSharpWrapper(L, csIndexer);
#else
				LuaAPI.lua_pushstdcallcfunction(L, csIndexer);
#endif
			}

			translator.Push(L, type == null ? base_type : type.BaseType());

			LuaAPI.xlua_pushasciistring(L, LuaIndexsFieldName);
			LuaAPI.lua_rawget(L, LuaIndexes.LUA_REGISTRYINDEX);
			if (arrayIndexer == null)
			{
				LuaAPI.lua_pushnil(L);
			}
			else
			{
#if GEN_CODE_MINIMIZE
                translator.PushCSharpWrapper(L, arrayIndexer);
#else
				LuaAPI.lua_pushstdcallcfunction(L, arrayIndexer);
#endif
			}

			LuaAPI.gen_obj_indexer(L);

			if (type != null)
			{
				LuaAPI.xlua_pushasciistring(L, LuaIndexsFieldName);
				LuaAPI.lua_rawget(L, LuaIndexes.LUA_REGISTRYINDEX);//store in lua indexs function tables
				translator.Push(L, type);
				LuaAPI.lua_pushvalue(L, -3);
				LuaAPI.lua_rawset(L, -3);
				LuaAPI.lua_pop(L, 1);
			}

			LuaAPI.lua_rawset(L, meta_idx);
			//end index gen

			//begin newindex gen
			LuaAPI.xlua_pushasciistring(L, "__newindex");
			LuaAPI.lua_pushvalue(L, setter_idx);

			if (csNewIndexer == null)
			{
				LuaAPI.lua_pushnil(L);
			}
			else
			{
#if GEN_CODE_MINIMIZE
                translator.PushCSharpWrapper(L, csNewIndexer);
#else
				LuaAPI.lua_pushstdcallcfunction(L, csNewIndexer);
#endif
			}

			translator.Push(L, type == null ? base_type : type.BaseType());

			LuaAPI.xlua_pushasciistring(L, LuaNewIndexsFieldName);
			LuaAPI.lua_rawget(L, LuaIndexes.LUA_REGISTRYINDEX);

			if (arrayNewIndexer == null)
			{
				LuaAPI.lua_pushnil(L);
			}
			else
			{
#if GEN_CODE_MINIMIZE
                translator.PushCSharpWrapper(L, arrayNewIndexer);
#else
				LuaAPI.lua_pushstdcallcfunction(L, arrayNewIndexer);
#endif
			}

			LuaAPI.gen_obj_newindexer(L);

			if (type != null)
			{
				LuaAPI.xlua_pushasciistring(L, LuaNewIndexsFieldName);
				LuaAPI.lua_rawget(L, LuaIndexes.LUA_REGISTRYINDEX);//store in lua newindexs function tables
				translator.Push(L, type);
				LuaAPI.lua_pushvalue(L, -3);
				LuaAPI.lua_rawset(L, -3);
				LuaAPI.lua_pop(L, 1);
			}

			LuaAPI.lua_rawset(L, meta_idx);
			//end new index gen
			LuaAPI.lua_pop(L, 4);
		}

#if GEN_CODE_MINIMIZE
        public static void RegisterFunc(RealStatePtr L, int idx, string name, CSharpWrapper func)
        {
            ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            idx = abs_idx(LuaAPI.lua_gettop(L), idx);
            LuaAPI.xlua_pushasciistring(L, name);
            translator.PushCSharpWrapper(L, func);
            LuaAPI.lua_rawset(L, idx);
        }
#else
		public static void RegisterFunc(RealStatePtr L, int idx, string name, LuaCSFunction func)
		{
			idx = abs_idx(LuaAPI.lua_gettop(L), idx);
			LuaAPI.xlua_pushasciistring(L, name);
			LuaAPI.lua_pushstdcallcfunction(L, func);
			LuaAPI.lua_rawset(L, idx);
		}
#endif

		public static void RegisterLazyFunc(RealStatePtr L, int idx, string name, Type type, LazyMemberTypes memberType, bool isStatic)
		{
			idx = abs_idx(LuaAPI.lua_gettop(L), idx);
			LuaAPI.xlua_pushasciistring(L, name);

			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			translator.PushAny(L, type);
			LuaAPI.xlua_pushinteger(L, (int)memberType);
			LuaAPI.lua_pushstring(L, name);
			LuaAPI.lua_pushboolean(L, isStatic);
			LuaAPI.lua_pushstdcallcfunction(L, InternalGlobals.LazyReflectionWrap, 4);
			LuaAPI.lua_rawset(L, idx);
		}

		public static void RegisterObject(RealStatePtr L, ObjectTranslator translator, int idx, string name, object obj)
		{
			idx = abs_idx(LuaAPI.lua_gettop(L), idx);
			LuaAPI.xlua_pushasciistring(L, name);
			translator.PushAny(L, obj);
			LuaAPI.lua_rawset(L, idx);
		}

#if GEN_CODE_MINIMIZE
        public static void BeginClassRegister(Type type, RealStatePtr L, CSharpWrapper creator, int class_field_count,
            int static_getter_count, int static_setter_count)
#else
		public static void BeginClassRegister(Type type, RealStatePtr L, LuaCSFunction creator, int class_field_count,
			int static_getter_count, int static_setter_count)
#endif
		{
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			LuaAPI.lua_createtable(L, 0, class_field_count);

			LuaAPI.xlua_pushasciistring(L, "UnderlyingSystemType");
			translator.PushAny(L, type);
			LuaAPI.lua_rawset(L, -3);

			int cls_table = LuaAPI.lua_gettop(L);

			SetCSTable(L, type, cls_table);

			LuaAPI.lua_createtable(L, 0, 3);
			int meta_table = LuaAPI.lua_gettop(L);
			if (creator != null)
			{
				LuaAPI.xlua_pushasciistring(L, "__call");
#if GEN_CODE_MINIMIZE
                translator.PushCSharpWrapper(L, creator);
#else
				LuaAPI.lua_pushstdcallcfunction(L, creator);
#endif
				LuaAPI.lua_rawset(L, -3);
			}

			if (static_getter_count == 0)
			{
				LuaAPI.lua_pushnil(L);
			}
			else
			{
				LuaAPI.lua_createtable(L, 0, static_getter_count);
			}

			if (static_setter_count == 0)
			{
				LuaAPI.lua_pushnil(L);
			}
			else
			{
				LuaAPI.lua_createtable(L, 0, static_setter_count);
			}
			LuaAPI.lua_pushvalue(L, meta_table);
			LuaAPI.lua_setmetatable(L, cls_table);
		}

		public const int CLS_IDX = -4;
		public const int CLS_META_IDX = -3;
		public const int CLS_GETTER_IDX = -2;
		public const int CLS_SETTER_IDX = -1;

		public static void EndClassRegister(Type type, RealStatePtr L, ObjectTranslator translator)
		{
			int top = LuaAPI.lua_gettop(L);
			int cls_idx = abs_idx(top, CLS_IDX);
			int cls_getter_idx = abs_idx(top, CLS_GETTER_IDX);
			int cls_setter_idx = abs_idx(top, CLS_SETTER_IDX);
			int cls_meta_idx = abs_idx(top, CLS_META_IDX);

			//begin cls index
			LuaAPI.xlua_pushasciistring(L, "__index");
			LuaAPI.lua_pushvalue(L, cls_getter_idx);
			LuaAPI.lua_pushvalue(L, cls_idx);
			translator.Push(L, type.BaseType());
			LuaAPI.xlua_pushasciistring(L, LuaClassIndexsFieldName);
			LuaAPI.lua_rawget(L, LuaIndexes.LUA_REGISTRYINDEX);
			LuaAPI.gen_cls_indexer(L);

			LuaAPI.xlua_pushasciistring(L, LuaClassIndexsFieldName);
			LuaAPI.lua_rawget(L, LuaIndexes.LUA_REGISTRYINDEX);//store in lua indexs function tables
			translator.Push(L, type);
			LuaAPI.lua_pushvalue(L, -3);
			LuaAPI.lua_rawset(L, -3);
			LuaAPI.lua_pop(L, 1);

			LuaAPI.lua_rawset(L, cls_meta_idx);
			//end cls index

			//begin cls newindex
			LuaAPI.xlua_pushasciistring(L, "__newindex");
			LuaAPI.lua_pushvalue(L, cls_setter_idx);
			translator.Push(L, type.BaseType());
			LuaAPI.xlua_pushasciistring(L, LuaClassNewIndexsFieldName);
			LuaAPI.lua_rawget(L, LuaIndexes.LUA_REGISTRYINDEX);
			LuaAPI.gen_cls_newindexer(L);

			LuaAPI.xlua_pushasciistring(L, LuaClassNewIndexsFieldName);
			LuaAPI.lua_rawget(L, LuaIndexes.LUA_REGISTRYINDEX);//store in lua newindexs function tables
			translator.Push(L, type);
			LuaAPI.lua_pushvalue(L, -3);
			LuaAPI.lua_rawset(L, -3);
			LuaAPI.lua_pop(L, 1);

			LuaAPI.lua_rawset(L, cls_meta_idx);
			//end cls newindex

			LuaAPI.lua_pop(L, 4);
		}

		//这里的path并不是该type真实存在的物理路径，而是其在代码结构中路径，与脚本物理存放路径无关
		static List<string> getPathOfType(Type type)
		{
			List<string> path = new List<string>();

			if (type.Namespace != null)
			{
				path.AddRange(type.Namespace.Split(new char[] { '.' }));
			}

			//“namespace.xxx”，则classname从“.”后第一位开始计算
			//经过实际测试：当获取任意对象的type时，type的string形式会自动包含其namespace
			string class_name = type.ToString().Substring(type.Namespace == null ? 0 : type.Namespace.Length + 1);

			if (type.IsNested)   //嵌套类：类名使用“+”串联
			{
				path.AddRange(class_name.Split(new char[] { '+' }));
			}
			else
			{
				path.Add(class_name);  //如不是嵌套类，则可直接使用该类名
			}
			return path;
		}

		public static void LoadCSTable(RealStatePtr L, Type type)
		{
			int oldTop = LuaAPI.lua_gettop(L);
            LuaAPI.xlua_pushasciistring(L, LuaEnv.CSHARP_NAMESPACE);
            LuaAPI.lua_rawget(L, LuaIndexes.LUA_REGISTRYINDEX);

            //获取该type的代码路径(部分类型，如nestedType本身并没有物理存放路径，所以这里只使用代码路径)
            List<string> path = getPathOfType(type);

			for (int i = 0; i < path.Count; ++i)
			{
				LuaAPI.xlua_pushasciistring(L, path[i]);
				if (0 != LuaAPI.xlua_pgettable(L, -2))
				{
					LuaAPI.lua_settop(L, oldTop);
					LuaAPI.lua_pushnil(L);
					return;
				}
				if (!LuaAPI.lua_istable(L, -1) && i < path.Count - 1)
				{
					LuaAPI.lua_settop(L, oldTop);
					LuaAPI.lua_pushnil(L);
					return;
				}
				LuaAPI.lua_remove(L, -2);
			}
		}

		public static void SetCSTable(RealStatePtr L, Type type, int cls_table)
		{
			int oldTop = LuaAPI.lua_gettop(L);
			//"abs_idx"作用仅仅只是将任意idx转换成正序的栈索引，方便后续计算
			cls_table = abs_idx(oldTop, cls_table);  //计算得到该table的正序索引

            LuaAPI.xlua_pushasciistring(L, LuaEnv.CSHARP_NAMESPACE);
            //获取注册表中key为“CSHARP_NAMESPACE”的value，并将该value入栈
            LuaAPI.lua_rawget(L, LuaIndexes.LUA_REGISTRYINDEX);

            //获取type的代码路径(非物理存放路径)
            List<string> path = getPathOfType(type);

			for (int i = 0; i < path.Count - 1; ++i)
			{
				LuaAPI.xlua_pushasciistring(L, path[i]);
				//由于将“path[i]”入栈，因此“CSHARP_NAMESPACE”的value处在“-2”的位置
				//“xlua_pgettable”：获取RegestryTable[CSHARP_NAMESPACE]中key为“path[i]”的value, 并将该value入栈
				//                  如果执行失败，则将错误信息入栈，并返回非0数值("0"代表正常执行)
				//注意："lua_gettable"的返回值是入栈元素的值类型lua_type，与此处“xlua_pgettable”返回值意义不同
				//C语言文件"lua.h"中定义的lua_type: LUA_TNIL - -1, LUA_TNIL - 0, LUA_TBOOLEAN - 1
				if (0 != LuaAPI.xlua_pgettable(L, -2))
				{
					//执行异常，此时栈顶存放错误信息
					var err = LuaAPI.lua_tostring(L, -1);
					LuaAPI.lua_settop(L, oldTop);  //恢复栈初始配置，然后抛出异常
					throw new Exception("SetCSTable for [" + type + "] error: " + err);
				}

				//当“path[i]”的value为nil时，则需要为其赋值
				if (LuaAPI.lua_isnil(L, -1))
				{
					//先将栈顶的nil值出栈，以便后续入栈“path[i]”的正确数值；此时栈顶元素为“CSHARP_NAMESPACE”的value
					LuaAPI.lua_pop(L, 1);
					LuaAPI.lua_createtable(L, 0, 0);  //入栈新table
					LuaAPI.xlua_pushasciistring(L, path[i]);
					//注意：此时栈中元素“-3”: CSHARP_NAMESPACE的table, "-2"：空表， “-1”：path[i]
					LuaAPI.lua_pushvalue(L, -2); //将“-2”索引处的值拷贝副本，并压入栈顶
					//此时栈中元素顺序：“-4”：CSHARP_NAMESPACE的table， “-3”：空表， “-2”： path[i](key), "-1"：空表副本(value)
					//因此这里是设置“CSHARP_NAMESPACE[key] = value” —— 非常重要
					LuaAPI.lua_rawset(L, -4);
					//此时栈中元素顺序：“-2”：CSHARP_NAMESPACE的table，“-1”：空表，并且CSHARP_NAMESPACE[path[i]] = {}
				}
				else if (!LuaAPI.lua_istable(L, -1))    //这里限定命名空间对应的value都是table
				{
					LuaAPI.lua_settop(L, oldTop);  //恢复栈初始配置
					throw new Exception("SetCSTable for [" + type + "] error: ancestors is not a table!");
				}
				LuaAPI.lua_remove(L, -2);  //移除栈中索引“-2”的元素，故CSHARP_NAMESPACE的table被移除，此时栈顶元素为空表

				//注意：每一次遍历结束后，栈顶存放的是该“path[i]”对应的value值，默认都是LUA_TTABLE

				//******* START: 重要问题 ***********
				//描述：为什么设置键值对时使用的是索引“-4”，并且每次遍历后移除的元素索引是“-2”
				//解答：
				//1.Type的命名空间存在层级递进的关系，并且命名空间存在多层嵌套，所以在设置第一层path[i]后，
				//  第二层的键值对应该在第一层的table中设立，而不在总的“CSHARP_NAMESPACE的table”中设立，
				//  如果某个总的命名空间下有多个子的命名空间，如"System.Text", "System.IO"，
				//  此时只需要在“path[i] = System”的value表中查找“Text”和“IO”
				//  因此每次迭代完毕后，栈顶存放的都是上次迭代的LUA_TTABLE值，以便继续进行下一次迭代
				//2.每次迭代后都移除“-2”，是为了避免在循环遍历的过程中，栈中元素过多，因此把不需要的元素都删除掉
				//******* END: 重要问题 ***********
			}

			//由于遍历条件限制“i < path.Count -1”，因此遍历结束后栈顶存放的是classname的上一次命名空间的LUA_TTABLE值
			LuaAPI.xlua_pushasciistring(L, path[path.Count - 1]);  //将classname作为key入栈
			LuaAPI.lua_pushvalue(L, cls_table);
			//在classname的直接父类的table中设置键值对，但注意：此时设置的value是栈L中的索引，而非直接的table
			//所以如果需要获取该classname的value值，则需要通过“lua_pushvalue(L, index)”压副本入栈顶
			//移除时则通过“lua_remove(L, index)”
			LuaAPI.lua_rawset(L, -3);
			//移除唯一剩下的命名空间table，此时栈恢复初始配置。至此该type在“CSHARP_NAMESPACE”表格中的的路径配置结束
			LuaAPI.lua_pop(L, 1);

			//重新将注册表中“CSHARP_NAMESPACE”对应的value表入栈
            LuaAPI.xlua_pushasciistring(L, LuaEnv.CSHARP_NAMESPACE);
            LuaAPI.lua_rawget(L, LuaIndexes.LUA_REGISTRYINDEX);

            ObjectTranslatorPool.Instance.Find(L).PushAny(L, type);
			LuaAPI.lua_pushvalue(L, cls_table);
			LuaAPI.lua_rawset(L, -3);
			LuaAPI.lua_pop(L, 1);
		}

		public const string LuaIndexsFieldName = "LuaIndexs";

		public const string LuaNewIndexsFieldName = "LuaNewIndexs";

		public const string LuaClassIndexsFieldName = "LuaClassIndexs";

		public const string LuaClassNewIndexsFieldName = "LuaClassNewIndexs";

		public static bool IsParamsMatch(MethodInfo delegateMethod, MethodInfo bridgeMethod)
		{
			if (delegateMethod == null || bridgeMethod == null)
			{
				return false;
			}
			if (delegateMethod.ReturnType != bridgeMethod.ReturnType)
			{
				return false;
			}
			ParameterInfo[] delegateParams = delegateMethod.GetParameters();
			ParameterInfo[] bridgeParams = bridgeMethod.GetParameters();
			if (delegateParams.Length != bridgeParams.Length)
			{
				return false;
			}

			for (int i = 0; i < delegateParams.Length; i++)
			{
				if (delegateParams[i].ParameterType != bridgeParams[i].ParameterType || delegateParams[i].IsOut != bridgeParams[i].IsOut)
				{
					return false;
				}
			}

            var lastPos = delegateParams.Length - 1;
            return lastPos < 0 || delegateParams[lastPos].IsDefined(typeof(ParamArrayAttribute), false) == bridgeParams[lastPos].IsDefined(typeof(ParamArrayAttribute), false);
		}

		public static bool IsSupportedMethod(MethodInfo method)
		{
			if (!method.ContainsGenericParameters)
				return true;
			var methodParameters = method.GetParameters();
			var returnType = method.ReturnType;
			var hasValidGenericParameter = false;
			var returnTypeValid = !returnType.IsGenericParameter;
			for (var i = 0; i < methodParameters.Length; i++)
			{
				var parameterType = methodParameters[i].ParameterType;
				if (parameterType.IsGenericParameter)
				{
					var parameterConstraints = parameterType.GetGenericParameterConstraints();
					if (parameterConstraints.Length == 0) return false;
					foreach (var parameterConstraint in parameterConstraints)
					{
						if (!parameterConstraint.IsClass() || (parameterConstraint == typeof(ValueType)))
							return false;
					}
					hasValidGenericParameter = true;
					if (!returnTypeValid)
					{
						if (parameterType == returnType)
						{
							returnTypeValid = true;
						}
					}
				}
			}
			return hasValidGenericParameter && returnTypeValid;
		}

		public static MethodInfo MakeGenericMethodWithConstraints(MethodInfo method)
		{
			try
			{
				var genericArguments = method.GetGenericArguments();
				var constraintedArgumentTypes = new Type[genericArguments.Length];
				for (var i = 0; i < genericArguments.Length; i++)
				{
					var argumentType = genericArguments[i];
					var parameterConstraints = argumentType.GetGenericParameterConstraints();
					constraintedArgumentTypes[i] = parameterConstraints[0];
				}
				return method.MakeGenericMethod(constraintedArgumentTypes);
			}
			catch (Exception)
			{
				return null;
			}
		}

		private static Type getExtendedType(MethodInfo method)
		{
			var type = method.GetParameters()[0].ParameterType;
			if (!type.IsGenericParameter)
				return type;
			var parameterConstraints = type.GetGenericParameterConstraints();
			if (parameterConstraints.Length == 0)
				throw new InvalidOperationException();

			var firstParameterConstraint = parameterConstraints[0];
			if (!firstParameterConstraint.IsClass())
				throw new InvalidOperationException();
			return firstParameterConstraint;
		}

		public static bool IsStaticPInvokeCSFunction(LuaCSFunction csFunction)
		{
#if UNITY_WSA && !UNITY_EDITOR
            return csFunction.GetMethodInfo().IsStatic && csFunction.GetMethodInfo().GetCustomAttribute<MonoPInvokeCallbackAttribute>() != null;
#else
			return csFunction.Method.IsStatic && Attribute.IsDefined(csFunction.Method, typeof(MonoPInvokeCallbackAttribute));
#endif
		}

		public static bool IsPublic(Type type)
		{
			if (type.IsNested)
			{
				if (!type.IsNestedPublic()) return false;
				return IsPublic(type.DeclaringType);
			}
			if (type.IsGenericType())
			{
				var gas = type.GetGenericArguments();
				for (int i = 0; i < gas.Length; i++)
				{
					if (!IsPublic(gas[i]))
					{
						return false;
					}
				}
			}
			return type.IsPublic();
		}
	}
}
