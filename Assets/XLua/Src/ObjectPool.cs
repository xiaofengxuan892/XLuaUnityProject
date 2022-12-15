/*
 * Tencent is pleased to support the open source community by making xLua available.
 * Copyright (C) 2016 THL A29 Limited, a Tencent company. All rights reserved.
 * Licensed under the MIT License (the "License"); you may not use this file except in compliance with the License. You may obtain a copy of the License at
 * http://opensource.org/licenses/MIT
 * Unless required by applicable law or agreed to in writing, software distributed under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the License for the specific language governing permissions and limitations under the License.
*/

using System;
using System.Collections;
using System.Collections.Generic;

namespace XLua
{
    public class ObjectPool
    {
        const int LIST_END = -1;
        const int ALLOCED = -2;
        struct Slot
        {
            public int next;
            public object obj;

            public Slot(int next, object obj)
            {
                this.next = next;
                this.obj = obj;
            }
        }

        private Slot[] list = new Slot[512];
        private int freelist = LIST_END;
        private int count = 0;

        public object this[int i]
        {
            get
            {
                if (i >= 0 && i < count)
                {
                    return list[i].obj;
                }

                return null;
            }
        }

        public void Clear()
        {
            freelist = LIST_END;
            count = 0;
            list = new Slot[512];
        }

        void extend_capacity()
        {
            Slot[] new_list = new Slot[list.Length * 2];
            for (int i = 0; i < list.Length; i++)
            {
                new_list[i] = list[i];
            }
            list = new_list;
        }

        //从该方法的执行逻辑来看，这里生成”index“的方法其实极度简单，甚至于没有对元素是否已经存在集合进行去重判断，
        //只是单纯的往集合中递增元素(在添加前会检测是否有上次释放的元素索引，以便重复利用，仅此而已)
        public int Add(object obj)
        {
            int index = LIST_END;

            //1."freelist"指代上次释放的元素的索引，这里由于使用数组集合，不方便随意删除元素，
            //  同时也是为了循环利用数组中的元素，因此将上次释放的元素索引记录下来，以便后面“Add”元素时直接使用
            //2.“freelist”, "ALLOCED"，”next“的命名其实”故弄玄虚“了
            //  "freelist"：上次释放的集合中元素索引值，以便后续”Add“新元素时直接使用。默认为”-1“
            //              如果当前没有释放的元素，或者新”Add“的元素已经顶替了之前释放的元素位置，此时都需要重置为”-1“
            //              因此可以直接根据”freelist“的数值是否为”-1“来判断当前”Add“元素的方式
            //  "ALLOCED"，”next“：作用基本类似，都是为了区分当前是否可以释放该元素
            //             next：1.如果当前该索引的元素还在集合中(根据next的值来判断)，
            //                    那么此时可以释放该元素，以供其它”Add“元素使用，同时需要设置next的数值；
            //                  2.如果该索引的元素当前已经被释放了(根据next的值来判断)，那么此时则无需处理
            //                  3.当”Add“新元素成功后，该索引的”next“会被重置，以便后续再次删除
            //  总结：”ALLOCED“, "next"其实从作用来看只是一种特殊状态标记，这里还专门封装了一个”Slot“结构，实在是无语，有”装逼“的嫌疑
            if (freelist != LIST_END)
            {
                //只有在释放过元素后才会执行本区域代码
                index = freelist;
                list[index].obj = obj;

                freelist = list[index].next;
                list[index].next = ALLOCED;
            }
            else
            {
                //初始添加obj时，会直接执行本区域代码。
                //注意：这里添加的每一个“Slot”对象的“next”参数都是“Alloced”
                if (count == list.Length)
                {
                    extend_capacity();
                }
                index = count;    //由于数组编号从0开始，故这里使用其作为新加入元素的index
                list[index] = new Slot(ALLOCED, obj);
                count = index + 1; //数组中真实元素数量为“index + 1”
            }

            return index;
        }

        public bool TryGetValue(int index, out object obj)
        {
            if (index >= 0 && index < count && list[index].next == ALLOCED)
            {
                obj = list[index].obj;
                return true;
            }

            obj = null;
            return false;
        }

        public object Get(int index)
        {
            if (index >= 0 && index < count)
            {
                return list[index].obj;
            }
            return null;
        }

        //当移除集合中的元素时，才会使得“Add”方法中执行区域有不同
        public object Remove(int index)
        {
            //当该索引的值可以被释放时(根据next数值来判断)
            if (index >= 0 && index < count && list[index].next == ALLOCED)
            {
                object o = list[index].obj;
                list[index].obj = null;
                list[index].next = freelist;
                freelist = index;   //记录当前释放的元素的索引
                return o;
            }

            return null;
        }

        public object Replace(int index, object o)
        {
            if (index >= 0 && index < count)
            {
                object obj = list[index].obj;
                list[index].obj = o;
                return obj;
            }

            return null;
        }

        public int Check(int check_pos, int max_check, Func<object, bool> checker, Dictionary<object, int> reverse_map)
        {
            if (count == 0)
            {
                return 0;
            }
            for (int i = 0; i < Math.Min(max_check, count); ++i)
            {
                check_pos %= count;
                if (list[check_pos].next == ALLOCED && !Object.ReferenceEquals(list[check_pos].obj, null))
                {
                    if (!checker(list[check_pos].obj))
                    {
                        /* 问题：
                         * 1.为什么使用”Replace“，而不直接使用”Remove“将数组集合中该元素直接删除呢？
                         * 解答：由于该"Check"方法的主要目的是为了解决Object被GameObejct.Destroy等方法直接销毁导致object为null的情况
                         *      由于此时该object的唯一id在Lua堆栈中的userdata可能依然在
                         *      如果直接使用”Remove“，那么该id就会被当成下一新”Add“元素的索引
                         *      在此基础上，如果通过userdata获取objectId，然后在”ObjectPool“的数组集合中查找时
                         *      由于该id已经被新”Add“的元素使用，那么此时通过”userdata“获取到的就是新的object，而不是该”userdata“实际对应的object
                         *      这就是不能使用”Remove“的原因。
                         *      而如果不更新”Remove“中导致的index，但是需要改变index中的obj，这也就是”Replace“方法需要实现的功能
                         * 2.当使用”Replace“方法后，”ObjectPool“中的数组集合index无法被下个新”Add“的元素循环利用，
                         *   这样是否会导致数组集合中”object“为null的元素越来越多，并且数组的容量越来越大？
                         * 解答：正常流程：
                         *      userdata被”__gc“释放，然后调用”StaticLuaCallbacks.LuaGC“方法来”translator.CollectObject“
                         *      这样正常释放object的引用，即将object置为null，并释放index索引给新”Add“的元素
                         *      非正常流程：
                         *      object被Destroy，但userdata依然还在被引用的情况。
                         *      此时object的引用已经被“Tick”方法中循环遍历解决了: object = null
                         *      那么此时只需要解决栈L中userdata的引用
                         *      所幸Lua中针对堆栈中元素的管理有固定机制：
                         *
                         *      ************ START: 重要机制 *************
                         *      Lua中提供了一个机制，会自动检测栈上的元素，如果该元素没有被其他地方引用则会释放该元素，将其出栈
                         *      ************ END: 重要机制 *************
                         *
                         *      因此即使Object为外界Destroy，此时在”ObjectPool“的数组集合中查询到该Object已经为null，
                         *      因此可根据条件”null != Object“来判断是否执行后续代码
                         *      并且最重要的是：根据Lua中的机制，如果长时间没有地方引用该userdata，则其会被自动释放
                         *      而”translator.CollectObject“也会根据”null != object”来决定是否回收该对象
                         */
                        object obj = Replace(check_pos, null);
                        int obj_index;
                        if (reverse_map.TryGetValue(obj, out obj_index) && obj_index == check_pos)
                        {
                            reverse_map.Remove(obj);
                        }
                    }
                }
                ++check_pos;
            }

            return check_pos %= count;
        }
    }
}