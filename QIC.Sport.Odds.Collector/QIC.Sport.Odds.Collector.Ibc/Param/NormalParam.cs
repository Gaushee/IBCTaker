﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using QIC.Sport.Odds.Collector.Core.SubscriptionManager;

namespace QIC.Sport.Odds.Collector.Ibc.Param
{
    public class NormalParam : BaseParam, ISubscribeParam
    {
        public string SubscribeType = "odds";
        public int Stage { get; set; }
        public SocketParam SocketParam { get; set; }
        public SyncTimeParam TimeParam { get; set; }
        public List<int> LimitMarketIdList = new List<int>() { 1, 2, 3, 4, 5, 6, 7, 8, 16 };
        public string Topic
        {
            get { return string.Format("42[\"subscribe\",\"" + SubscribeType + "\",{0}]", SubscribeType == "odds" ? JsonConvert.SerializeObject(SocketParam) : JsonConvert.SerializeObject(TimeParam)); }
        }

        public string Key
        {
            get { return SocketParam != null ? SocketParam.id : TimeParam.id; }
        }
    }

    public class SocketParam
    {
        public string id { get; set; }
        [JsonIgnore]
        public string RMark { get; set; }
        public int rev { get; set; }
        public Condition condition { get; set; }
    }

    public class SyncTimeParam
    {
        public string id { get; set; }
        public int rev { get; set; }
        public string condition { get; set; }
    }

    public class Condition
    {
        //[JsonIgnore]    //  忽略sporttype可获取所有运动的数据
        public int[] sporttype { get; set; }
        public string marketid { get; set; }
        public int[] bettype { get; set; }
        public string sorting { get; set; }
    }
}
