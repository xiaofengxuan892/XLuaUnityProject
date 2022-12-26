/*
 * Tencent is pleased to support the open source community by making xLua available.
 * Copyright (C) 2016 THL A29 Limited, a Tencent company. All rights reserved.
 * Licensed under the MIT License (the "License"); you may not use this file except in compliance with the License. You may obtain a copy of the License at
 * http://opensource.org/licenses/MIT
 * Unless required by applicable law or agreed to in writing, software distributed under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the License for the specific language governing permissions and limitations under the License.
*/

#if USE_UNI_LUA
using LuaAPI = UniLua.Lua;
using RealStatePtr = UniLua.ILuaState;
using LuaCSFunction = UniLua.CSharpFunctionDelegate;
#else
using LuaAPI = XLua.LuaDLL.Lua;
using RealStatePtr = System.IntPtr;
using LuaCSFunction = XLua.LuaDLL.lua_CSFunction;
#endif
using System.IO;
using System.Text;
using UnityEngine;


namespace XLua
{
    using System;
    using System.Collections.Generic;

    public class LuaEnv : IDisposable
    {
        public const string CSHARP_NAMESPACE = "xlua_csharp_namespace";
        public const string MAIN_SHREAD = "xlua_main_thread";

        internal RealStatePtr rawL;

        internal RealStatePtr L
        {
            get
            {
                if (rawL == RealStatePtr.Zero)
                {
                    throw new InvalidOperationException("this lua env had disposed!");
                }
                return rawL;
            }
        }

        private LuaTable _G;

        internal ObjectTranslator translator;

        internal int errorFuncRef = -1;

#if THREAD_SAFE || HOTFIX_ENABLE
        internal /*static*/ object luaLock = new object();

        internal object luaEnvLock
        {
            get
            {
                return luaLock;
            }
        }
#endif

        const int LIB_VERSION_EXPECT = 105;

        public LuaEnv()
        {
            if (LuaAPI.xlua_get_lib_version() != LIB_VERSION_EXPECT)
            {
                throw new InvalidProgramException("wrong lib version expect:"
                    + LIB_VERSION_EXPECT + " but got:" + LuaAPI.xlua_get_lib_version());
            }

#if THREAD_SAFE || HOTFIX_ENABLE
            lock(luaEnvLock)
#endif
            {
                LuaIndexes.LUA_REGISTRYINDEX = LuaAPI.xlua_get_registry_index();
#if GEN_CODE_MINIMIZE
                LuaAPI.xlua_set_csharp_wrapper_caller(InternalGlobals.CSharpWrapperCallerPtr);
#endif
                //创建lua堆栈
                rawL = LuaAPI.luaL_newstate();

                //初始化堆栈中的xlua标准库
                LuaAPI.luaopen_xlua(rawL);    //设置全局变量“xlua”的值(table形式，包含xlualib数组中的所有方法，在“xlua.c”文件中)
                LuaAPI.luaopen_i64lib(rawL);  //设置全局变量“uint64”的值(table形式，包含各种操作符元方法 —— 在“i64lib.c”文件中)

                translator = new ObjectTranslator(this, rawL);
                //为“LuaCSFunction”类型创建元表，并在注册表中设立键值对，同时在“typeIdMap”中存储该key，
                //以便直接通过key获取到注册表中的元表值
                translator.createFunctionMetatable(rawL);
                translator.OpenLib(rawL);
                //在“ObjectTranslatorPool”中将“translator”与使用的“rawL”绑定起来
                //PS: 虽然名字是“xxxPool”，但实际只是个集合的作用，并没有循环利用的功能
                ObjectTranslatorPool.Instance.Add(rawL, translator);

                //设置lua错误处理函数：lua代码在执行异常时会调用该方法
                LuaAPI.lua_atpanic(rawL, StaticLuaCallbacks.Panic);

#if !XLUA_GENERAL
                //设置lua日志输出函数print执行逻辑：StaticLuaCallbacks.Print，并绑定全局变量“print”
                LuaAPI.lua_pushstdcallcfunction(rawL, StaticLuaCallbacks.Print);
                if (0 != LuaAPI.xlua_setglobal(rawL, "print"))
                {
                    throw new Exception("call xlua_setglobal fail!");
                }
#endif

                //template engine lib register
                TemplateEngine.LuaTemplate.OpenLib(rawL);  //其实就是设置全局变量“template”，并添加“compile”和“execute”方法

                AddSearcher(StaticLuaCallbacks.LoadBuiltinLib, 2); // just after the preload searcher
                AddSearcher(StaticLuaCallbacks.LoadFromCustomLoaders, 3);
#if !XLUA_GENERAL
                AddSearcher(StaticLuaCallbacks.LoadFromResource, 4);
                AddSearcher(StaticLuaCallbacks.LoadFromStreamingAssetsPath, -1);
#endif
                /*********** 核心模块 ***********
                 * 作用：正式启动lua相关功能
                 */
                DoString(init_xlua, "Init");
                init_xlua = null;

#if (!UNITY_SWITCH && !UNITY_WEBGL) || UNITY_EDITOR
                AddBuildin("socket.core", StaticLuaCallbacks.LoadSocketCore);
                AddBuildin("socket", StaticLuaCallbacks.LoadSocketCore);
#endif

                AddBuildin("CS", StaticLuaCallbacks.LoadCS);

                LuaAPI.lua_newtable(rawL); //metatable of indexs and newindexs functions
                LuaAPI.xlua_pushasciistring(rawL, "__index");
                LuaAPI.lua_pushstdcallcfunction(rawL, StaticLuaCallbacks.MetaFuncIndex);
                LuaAPI.lua_rawset(rawL, -3);

                LuaAPI.xlua_pushasciistring(rawL, Utils.LuaIndexsFieldName);
                LuaAPI.lua_newtable(rawL);
                LuaAPI.lua_pushvalue(rawL, -3);
                LuaAPI.lua_setmetatable(rawL, -2);   //将包含“__index”的table设置成此空表的元表
                LuaAPI.lua_rawset(rawL, LuaIndexes.LUA_REGISTRYINDEX);

                LuaAPI.xlua_pushasciistring(rawL, Utils.LuaNewIndexsFieldName);
                LuaAPI.lua_newtable(rawL);
                LuaAPI.lua_pushvalue(rawL, -3);
                LuaAPI.lua_setmetatable(rawL, -2);
                LuaAPI.lua_rawset(rawL, LuaIndexes.LUA_REGISTRYINDEX);

                LuaAPI.xlua_pushasciistring(rawL, Utils.LuaClassIndexsFieldName);
                LuaAPI.lua_newtable(rawL);
                LuaAPI.lua_pushvalue(rawL, -3);
                LuaAPI.lua_setmetatable(rawL, -2);
                LuaAPI.lua_rawset(rawL, LuaIndexes.LUA_REGISTRYINDEX);

                LuaAPI.xlua_pushasciistring(rawL, Utils.LuaClassNewIndexsFieldName);
                LuaAPI.lua_newtable(rawL);
                LuaAPI.lua_pushvalue(rawL, -3);
                LuaAPI.lua_setmetatable(rawL, -2);
                LuaAPI.lua_rawset(rawL, LuaIndexes.LUA_REGISTRYINDEX);

                LuaAPI.lua_pop(rawL, 1); // pop metatable of indexs and newindexs functions

                LuaAPI.xlua_pushasciistring(rawL, MAIN_SHREAD);
                LuaAPI.lua_pushthread(rawL);
                LuaAPI.lua_rawset(rawL, LuaIndexes.LUA_REGISTRYINDEX);

                LuaAPI.xlua_pushasciistring(rawL, CSHARP_NAMESPACE);
                if (0 != LuaAPI.xlua_getglobal(rawL, "CS"))
                {
                    throw new Exception("get CS fail!");
                }
                LuaAPI.lua_rawset(rawL, LuaIndexes.LUA_REGISTRYINDEX);

#if !XLUA_GENERAL && (!UNITY_WSA || UNITY_EDITOR)
                translator.Alias(typeof(Type), "System.MonoType");
#endif

                if (0 != LuaAPI.xlua_getglobal(rawL, "_G"))
                {
                    throw new Exception("get _G fail!");
                }
                translator.Get(rawL, -1, out _G);
                LuaAPI.lua_pop(rawL, 1);

                //根据“xlua.c”文件中对“get_error_func_ref”执行逻辑可知：
                //这里是将“error_func”函数作为闭包压入栈中作为新的“栈顶元素”，然后使用“lua_ref”在注册表中为其生成key
                errorFuncRef = LuaAPI.get_error_func_ref(rawL);

                if (initers != null)
                {
                    for (int i = 0; i < initers.Count; i++)
                    {
                        initers[i](this, translator);
                    }
                }

                translator.CreateArrayMetatable(rawL);
                translator.CreateDelegateMetatable(rawL);
                translator.CreateEnumerablePairs(rawL);

                //************ START: 自定义Lua文件的load方法 ***********
                //为了方便自由加载Lua脚本，这里创建“CustomLoader”
                CustomLoader loadLuaFiles = LoadCustomLuaFiles;
                AddLoader(loadLuaFiles);
                //************ END: 自定义Lua文件的load方法 ***********
            }
        }

        //自定义的加载Lua文件的方法，
        //注意：这里传入的参数是文件名，而非该文件的完整路径(该参数由Lua代码中的“require ‘xxx’”决定)
        private byte[] LoadCustomLuaFiles(ref string filename) {
            if (string.IsNullOrEmpty(filename)) {
                return null;
            }

            //由于Lua中不方便使用“/”，故这里将代码中的“.”转换成“/”再使用
            var file = filename.Replace('.', '/') + ".lua";
            var path = Application.dataPath + "/XLua/Game/" + file;
            if (!File.Exists(path)) {
                return null;
            }

            //注意：这里不能直接返回“File.ReadAllBytes”读取到的byte[]，在执行Lua代码块时会报错
            var content = File.ReadAllText(path);
            //所有的Lua文件默认使用“UTF8”编码，解析byte[]时也使用UTF8解码以获取chunk代码块
            return Encoding.UTF8.GetBytes(content);
        }

        private static List<Action<LuaEnv, ObjectTranslator>> initers = null;

        public static void AddIniter(Action<LuaEnv, ObjectTranslator> initer)
        {
            if (initers == null)
            {
                initers = new List<Action<LuaEnv, ObjectTranslator>>();
            }
            initers.Add(initer);
        }

        //存储lua脚本运行中所有的全局变量
        public LuaTable Global
        {
            get
            {
                return _G;
            }
        }

        public T LoadString<T>(byte[] chunk, string chunkName = "chunk", LuaTable env = null)
        {
#if THREAD_SAFE || HOTFIX_ENABLE
            lock (luaEnvLock)
            {
#endif
                if (typeof(T) != typeof(LuaFunction) && !typeof(T).IsSubclassOf(typeof(Delegate)))
                {
                    throw new InvalidOperationException(typeof(T).Name + " is not a delegate type nor LuaFunction");
                }
                var _L = L;
                int oldTop = LuaAPI.lua_gettop(_L);

                if (LuaAPI.xluaL_loadbuffer(_L, chunk, chunk.Length, chunkName) != 0)
                    ThrowExceptionFromError(oldTop);

                if (env != null)
                {
                    env.push(_L);
                    LuaAPI.lua_setfenv(_L, -2);
                }

                T result = (T)translator.GetObject(_L, -1, typeof(T));
                LuaAPI.lua_settop(_L, oldTop);

                return result;
#if THREAD_SAFE || HOTFIX_ENABLE
            }
#endif
        }

        public T LoadString<T>(string chunk, string chunkName = "chunk", LuaTable env = null)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(chunk);
            return LoadString<T>(bytes, chunkName, env);
        }

        public LuaFunction LoadString(string chunk, string chunkName = "chunk", LuaTable env = null)
        {
            return LoadString<LuaFunction>(chunk, chunkName, env);
        }

        public object[] DoString(byte[] chunk, string chunkName = "chunk", LuaTable env = null)
        {
#if THREAD_SAFE || HOTFIX_ENABLE
            lock (luaEnvLock)
            {
#endif
                var _L = L;
                int oldTop = LuaAPI.lua_gettop(_L);
                int errFunc = LuaAPI.load_error_func(_L, errorFuncRef);
                if (LuaAPI.xluaL_loadbuffer(_L, chunk, chunk.Length, chunkName) == 0)
                {
                    if (env != null)
                    {
                        env.push(_L);   //根据该env的luaReference将其值入栈，此时栈顶元素为“运行环境”
                        //此时“chunk”在栈中索引为“-2”，这里将栈顶的“运行环境”设置为本“chunk”的运行环境
                        LuaAPI.lua_setfenv(_L, -2);
                    }

                    //这里执行的是纯Lua函数，因此无需参数；只有在调用“LuaCSFunction”时才考虑参数个数
                    if (LuaAPI.lua_pcall(_L, 0, -1, errFunc) == 0)
                    {
                        LuaAPI.lua_remove(_L, errFunc);
                        return translator.popValues(_L, oldTop); //执行成功后将返回值弹出，并恢复栈初始配置
                    }
                    else
                        ThrowExceptionFromError(oldTop);
                }
                else
                    ThrowExceptionFromError(oldTop);

                return null;
#if THREAD_SAFE || HOTFIX_ENABLE
            }
#endif
        }

        public object[] DoString(string chunk, string chunkName = "chunk", LuaTable env = null)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(chunk);
            return DoString(bytes, chunkName, env);
        }

        private void AddSearcher(LuaCSFunction searcher, int index)
        {
#if THREAD_SAFE || HOTFIX_ENABLE
            lock (luaEnvLock)
            {
#endif
                var _L = L;
                //"loader"中依次防止多个加载器需要的方法(LuaCSFunction)
                //如：“LoadBuiltinLib”, "LoadFromCustomLoaders", "LoadFromResource", "LoadFromStreamingAssetsPath"等
                //PS：由于代码最终是在Lua中运行，因此这里使用Table格式存储，但通过指针的形式将“LuaCSFunction”进行绑定
                LuaAPI.xlua_getloaders(_L);  //大概使用的是默认loader(table形式)，并入栈
                if (!LuaAPI.lua_istable(_L, -1))
                {
                    throw new Exception("Can not set searcher!");
                }
                uint len = LuaAPI.xlua_objlen(_L, -1);  //获取栈顶loader中table元素个数
                index = index < 0 ? (int)(len + index + 2) : index;
                //不论如何遍历，栈顶元素必然是“loader”的table
                for (int e = (int)len + 1; e > index; e--)
                {
                    //先获取“loader”的table中key为“e -1”的value，并将该value入栈
                    LuaAPI.xlua_rawgeti(_L, -1, e - 1);
                    //由于有“table[e-1]”入栈，因此table的索引变为“-2”
                    LuaAPI.xlua_rawseti(_L, -2, e); //table[e] = table[e-1]
                }
                //遍历结束后，栈顶元素依然是“loader”的table

                LuaAPI.lua_pushstdcallcfunction(_L, searcher);
                //由于有“LuaCSFunction”入栈，因此栈顶元素“loader”的table索引变为“-2”
                LuaAPI.xlua_rawseti(_L, -2, index); //table[index] = pointer(searcherFunc)
                LuaAPI.lua_pop(_L, 1);  //栈顶元素table出栈，恢复栈初始配置
#if THREAD_SAFE || HOTFIX_ENABLE
            }
#endif
        }

        public void Alias(Type type, string alias)
        {
            translator.Alias(type, alias);
        }

#if !XLUA_GENERAL
        int last_check_point = 0;

        int max_check_per_tick = 20;

        static bool ObjectValidCheck(object obj)
        {
            return (!(obj is UnityEngine.Object)) ||  ((obj as UnityEngine.Object) != null);
        }

        Func<object, bool> object_valid_checker = new Func<object, bool>(ObjectValidCheck);
#endif

        public void Tick()
        {
#if THREAD_SAFE || HOTFIX_ENABLE
            lock (luaEnvLock)
            {
#endif
                var _L = L;
                lock (refQueue)
                {
                    while (refQueue.Count > 0)
                    {
                        GCAction gca = refQueue.Dequeue();
                        translator.ReleaseLuaBase(_L, gca.Reference, gca.IsDelegate);
                    }
                }
#if !XLUA_GENERAL
                //管理非正常流程被释放的“ObjectPool”数组集合中的元素：如object被“GameObject.Destroy”等销毁
                last_check_point = translator.objects.Check(last_check_point, max_check_per_tick, object_valid_checker, translator.reverseMap);
#endif
#if THREAD_SAFE || HOTFIX_ENABLE
            }
#endif
        }

        //兼容API
        public void GC()
        {
            Tick();
        }

        public LuaTable NewTable()
        {
#if THREAD_SAFE || HOTFIX_ENABLE
            lock (luaEnvLock)
            {
#endif
                var _L = L;
                int oldTop = LuaAPI.lua_gettop(_L);

                LuaAPI.lua_newtable(_L);
                //将新入栈的空table转换成LuaTable返回给C#使用
                LuaTable returnVal = (LuaTable)translator.GetObject(_L, -1, typeof(LuaTable));

                LuaAPI.lua_settop(_L, oldTop);
                return returnVal;
#if THREAD_SAFE || HOTFIX_ENABLE
            }
#endif
        }

        private bool disposed = false;

        public void Dispose()
        {
            FullGc();
            System.GC.Collect();
            System.GC.WaitForPendingFinalizers();

            Dispose(true);

            System.GC.Collect();
            System.GC.WaitForPendingFinalizers();
        }

        public virtual void Dispose(bool dispose)
        {
#if THREAD_SAFE || HOTFIX_ENABLE
            lock (luaEnvLock)
            {
#endif
                if (disposed) return;
                Tick();

                if (!translator.AllDelegateBridgeReleased())
                {
                    throw new InvalidOperationException("try to dispose a LuaEnv with C# callback!");
                }

                //移除字典集合“translators”中该“L”元素
                ObjectTranslatorPool.Instance.Remove(L);

                LuaAPI.lua_close(L);   //关闭该状态机以释放内存
                translator = null;

                rawL = IntPtr.Zero;    //为什么“lua_close、Remove”中使用的是“L”，这里又故弄玄虚的使用“rawL”？

                disposed = true;
#if THREAD_SAFE || HOTFIX_ENABLE
            }
#endif
        }

        public void ThrowExceptionFromError(int oldTop)
        {
#if THREAD_SAFE || HOTFIX_ENABLE
            lock (luaEnvLock)
            {
#endif
                object err = translator.GetObject(L, -1);
                LuaAPI.lua_settop(L, oldTop);

                // A pre-wrapped exception - just rethrow it (stack trace of InnerException will be preserved)
                Exception ex = err as Exception;
                if (ex != null) throw ex;

                // A non-wrapped Lua error (best interpreted as a string) - wrap it and throw it
                if (err == null) err = "Unknown Lua Error";
                throw new LuaException(err.ToString());
#if THREAD_SAFE || HOTFIX_ENABLE
            }
#endif
        }

        internal struct GCAction
        {
            public int Reference;
            public bool IsDelegate;
        }

        Queue<GCAction> refQueue = new Queue<GCAction>();

        internal void equeueGCAction(GCAction action)
        {
            lock (refQueue)
            {
                refQueue.Enqueue(action);
            }
        }

        private string init_xlua = @" 
            local metatable = {}    -- 只是一个普通的table，这里依据其特殊作用，故命名为“metatable”
            local rawget = rawget   -- 全局变量“rawget”
            local setmetatable = setmetatable       -- 全局变量“setmetatable”
            local import_type = xlua.import_type    -- 全局变量“xlua”中的元素，key为：import_type
            local import_generic_type = xlua.import_generic_type
            local load_assembly = xlua.load_assembly

            function metatable:__index(key) 
                local fqn = rawget(self,'.fqn')   -- 获取metatable中key为”.fqn“的元素值
                fqn = ((fqn and fqn .. '.') or '') .. key

                local obj = import_type(fqn)

                if obj == nil then
                    -- It might be an assembly, so we load it too.
                    obj = { ['.fqn'] = fqn }
                    setmetatable(obj, metatable)
                elseif obj == true then
                    return rawget(self, key)
                end

                -- Cache this lookup
                rawset(self, key, obj)
                return obj
            end

            function metatable:__newindex()
                error('No such type: ' .. rawget(self,'.fqn'), 2)
            end

            -- A non-type has been called; e.g. foo = System.Foo()
            function metatable:__call(...)
                local n = select('#', ...)
                local fqn = rawget(self,'.fqn')
                if n > 0 then
                    local gt = import_generic_type(fqn, ...)
                    if gt then
                        return rawget(CS, gt)
                    end
                end
                error('No such type: ' .. fqn, 2)
            end

            CS = CS or {}
            setmetatable(CS, metatable)

            typeof = function(t) return t.UnderlyingSystemType end
            cast = xlua.cast
            if not setfenv or not getfenv then
                local function getfunction(level)
                    local info = debug.getinfo(level + 1, 'f')
                    return info and info.func
                end

                function setfenv(fn, env)
                  if type(fn) == 'number' then fn = getfunction(fn + 1) end
                  local i = 1
                  while true do
                    local name = debug.getupvalue(fn, i)
                    if name == '_ENV' then
                      debug.upvaluejoin(fn, i, (function()
                        return env
                      end), 1)
                      break
                    elseif not name then
                      break
                    end

                    i = i + 1
                  end

                  return fn
                end

                function getfenv(fn)
                  if type(fn) == 'number' then fn = getfunction(fn + 1) end
                  local i = 1
                  while true do
                    local name, val = debug.getupvalue(fn, i)
                    if name == '_ENV' then
                      return val
                    elseif not name then
                      break
                    end
                    i = i + 1
                  end
                end
            end

            xlua.hotfix = function(cs, field, func)
                if func == nil then func = false end
                local tbl = (type(field) == 'table') and field or {[field] = func}
                for k, v in pairs(tbl) do
                    local cflag = ''
                    if k == '.ctor' then
                        cflag = '_c'
                        k = 'ctor'
                    end
                    local f = type(v) == 'function' and v or nil
                    xlua.access(cs, cflag .. '__Hotfix0_'..k, f) -- at least one
                    pcall(function()
                        for i = 1, 99 do
                            xlua.access(cs, cflag .. '__Hotfix'..i..'_'..k, f)
                        end
                    end)
                end
                xlua.private_accessible(cs)
            end
            xlua.getmetatable = function(cs)
                return xlua.metatable_operation(cs)
            end
            xlua.setmetatable = function(cs, mt)
                return xlua.metatable_operation(cs, mt)
            end
            xlua.setclass = function(parent, name, impl)
                impl.UnderlyingSystemType = parent[name].UnderlyingSystemType
                rawset(parent, name, impl)
            end
            
            local base_mt = {
                __index = function(t, k)
                    local csobj = t['__csobj']
                    local func = csobj['<>xLuaBaseProxy_'..k]
                    return function(_, ...)
                         return func(csobj, ...)
                    end
                end
            }
            base = function(csobj)
                return setmetatable({__csobj = csobj}, base_mt)
            end
            ";

        public delegate byte[] CustomLoader(ref string filepath);

        internal List<CustomLoader> customLoaders = new List<CustomLoader>();

        //loader : CustomLoader， filepath参数：（ref类型）输入是require的参数，如果需要支持调试，需要输出真实路径。
        //                        返回值：如果返回null，代表加载该源下无合适的文件，否则返回UTF8编码的byte[]
        public void AddLoader(CustomLoader loader)
        {
            //所有的“CustomLoader”都有一个参数“filepath”，因此在调用C#方法时，传过来的第一个参数必然也是filepath
            customLoaders.Add(loader);
        }

        internal Dictionary<string, LuaCSFunction> buildin_initer = new Dictionary<string, LuaCSFunction>();

        public void AddBuildin(string name, LuaCSFunction initer)
        {
            if (!Utils.IsStaticPInvokeCSFunction(initer))
            {
                throw new Exception("initer must be static and has MonoPInvokeCallback Attribute!");
            }
            buildin_initer.Add(name, initer);
        }

        //The garbage-collector pause controls how long the collector waits before starting a new cycle.
        //Larger values make the collector less aggressive. Values smaller than 100 mean the collector
        //will not wait to start a new cycle. A value of 200 means that the collector waits for the total
        //memory in use to double before starting a new cycle.
        public int GcPause
        {
            get
            {
#if THREAD_SAFE || HOTFIX_ENABLE
                lock (luaEnvLock)
                {
#endif
                    int val = LuaAPI.lua_gc(L, LuaGCOptions.LUA_GCSETPAUSE, 200);
                    LuaAPI.lua_gc(L, LuaGCOptions.LUA_GCSETPAUSE, val);
                    return val;
#if THREAD_SAFE || HOTFIX_ENABLE
                }
#endif
            }
            set
            {
#if THREAD_SAFE || HOTFIX_ENABLE
                lock (luaEnvLock)
                {
#endif
                    LuaAPI.lua_gc(L, LuaGCOptions.LUA_GCSETPAUSE, value);
#if THREAD_SAFE || HOTFIX_ENABLE
                }
#endif
            }
        }

        //The step multiplier controls the relative speed of the collector relative to memory allocation.
        //Larger values make the collector more aggressive but also increase the size of each incremental
        //step. Values smaller than 100 make the collector too slow and can result in the collector never
        //finishing a cycle. The default, 200, means that the collector runs at "twice" the speed of memory
        //allocation.
        public int GcStepmul
        {
            get
            {
#if THREAD_SAFE || HOTFIX_ENABLE
                lock (luaEnvLock)
                {
#endif
                    int val = LuaAPI.lua_gc(L, LuaGCOptions.LUA_GCSETSTEPMUL, 200);
                    LuaAPI.lua_gc(L, LuaGCOptions.LUA_GCSETSTEPMUL, val);
                    return val;
#if THREAD_SAFE || HOTFIX_ENABLE
                }
#endif
            }
            set
            {
#if THREAD_SAFE || HOTFIX_ENABLE
                lock (luaEnvLock)
                {
#endif
                    LuaAPI.lua_gc(L, LuaGCOptions.LUA_GCSETSTEPMUL, value);
#if THREAD_SAFE || HOTFIX_ENABLE
                }
#endif
            }
        }

        public void FullGc()
        {
#if THREAD_SAFE || HOTFIX_ENABLE
            lock (luaEnvLock)
            {
#endif
                LuaAPI.lua_gc(L, LuaGCOptions.LUA_GCCOLLECT, 0);
#if THREAD_SAFE || HOTFIX_ENABLE
            }
#endif
        }

        public void StopGc()
        {
#if THREAD_SAFE || HOTFIX_ENABLE
            lock (luaEnvLock)
            {
#endif
                LuaAPI.lua_gc(L, LuaGCOptions.LUA_GCSTOP, 0);
#if THREAD_SAFE || HOTFIX_ENABLE
            }
#endif
        }

        public void RestartGc()
        {
#if THREAD_SAFE || HOTFIX_ENABLE
            lock (luaEnvLock)
            {
#endif
                LuaAPI.lua_gc(L, LuaGCOptions.LUA_GCRESTART, 0);
#if THREAD_SAFE || HOTFIX_ENABLE
            }
#endif
        }

        public bool GcStep(int data)
        {
#if THREAD_SAFE || HOTFIX_ENABLE
            lock (luaEnvLock)
            {
#endif
                return LuaAPI.lua_gc(L, LuaGCOptions.LUA_GCSTEP, data) != 0;
#if THREAD_SAFE || HOTFIX_ENABLE
            }
#endif
        }

        public int Memroy
        {
            get
            {
#if THREAD_SAFE || HOTFIX_ENABLE
                lock (luaEnvLock)
                {
#endif
                    return LuaAPI.lua_gc(L, LuaGCOptions.LUA_GCCOUNT, 0);
#if THREAD_SAFE || HOTFIX_ENABLE
                }
#endif
            }
        }
    }
}
