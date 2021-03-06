﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using HtmlAgilityPack;
using log4net;
using ML.NetComponent.Http;
using ML.Security;
using Newtonsoft.Json;
using QIC.Sport.Odds.Collector.Common;
using QIC.Sport.Odds.Collector.Core.SubscriptionManager;
using QIC.Sport.Odds.Collector.Ibc.Dto;
using QIC.Sport.Odds.Collector.Ibc.Param;
using MD5 = System.Security.Cryptography.MD5;
using Timer = System.Timers.Timer;

namespace QIC.Sport.Odds.Collector.Ibc.Taker
{
    public class PullTaker
    {
        private ILog logger = LogManager.GetLogger(typeof(PullTaker));
        private int takerID;
        private const string userAgent = "Mozilla/5.0 (Windows NT 6.3; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/54.0.2840.99 Safari/537.36";
        private const string accept = "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8";
        private const string acceptEncoding = "gzip, deflate, sdch";
        private LoginParam loginParam;
        private string localUrl;
        private Timer loginCheckTimer;
        private string checkCookie;
        private string checkUrl;

        public bool Available { get; set; }
        public TakerStatus Status { get; set; }
        public SocketInitDto SocketInitDto { get; set; }
        public Action ForceCloseSocket = null;
        public PullTaker()
        {
            Status = TakerStatus.Stoped;
            loginCheckTimer = new Timer();
            loginCheckTimer.Interval = 60 * 1000;
            loginCheckTimer.AutoReset = true;
            loginCheckTimer.Elapsed += LoginCheckin;
        }
        public void Init(ISubscribeParam param)
        {
            loginParam = param as LoginParam;
        }
        public bool AutoLogin()
        {
            try
            {
                Available = false;
                Console.WriteLine("Start login...");

                //  test跳过登录
                //if (beginDataID > 0)
                //{
                //    _nextTime = "";//清空
                //    available = true;
                //    Pause = false;
                //    ErrorTimes = 0;
                //    LoginResult = new LoginResult() { LoginSuccess = true };
                //    return true;
                //}

                HttpHelper http = new HttpHelper();
                //访问首页
                HttpItem item;
                HttpResult result;
                string mainCookie = "";// "LangKey=en";

                ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

                item = new HttpItem()
                {
                    URL = loginParam.WebUrl + "/Default.aspx?IsSSL=1",
                    UserAgent = userAgent,
                    Accept = accept,
                    Cookie = mainCookie,
                    Allowautoredirect = true,
                    KeepAlive = true
                };
                item.Header.Add("Accept-Language", "en-US;q=0.5,en;q=0.3");
                item.Header.Add("Accept-Encoding", acceptEncoding);
                item.Header.Add("Upgrade-Insecure-Requests", "1");
                result = http.GetHtml(item);
                string tmpCookie;
                if (!IndexPageCheck(result, item, out tmpCookie))
                {
                    Console.WriteLine("MessageBox|Index page Robots check failed over 3 times!!!");
                    return false;
                }
                mainCookie = HttpCookieHelper.CombineCookie(mainCookie, tmpCookie);

                //  首页访问成功
                if (result.StatusCode == HttpStatusCode.OK)
                {
                    //  检查IBC是否维护
                    if (result.Html.Contains("window.location.href='index.aspx"))
                    {
                        Console.WriteLine("maxbet website is under maintenance!!!");
                        logger.Debug("MessageBox|maxbet website is under maintenance!!!");
                        return false;
                    }

                    //  请求用DeviceID作为cookie请求JS
                    var item1 = new HttpItem()
                    {
                        URL = "https://sc.detecas.com/di/activator.ashx",
                        UserAgent = userAgent,
                        Accept = "*/*",
                        Referer = loginParam.WebUrl + "/Default.aspx?IsSSL=1",
                        Host = "sc.detecas.com",
                        KeepAlive = true,
                        //  只根据浏览器信息生成的，固定不变
                    };
                    item1.Header.Add("Accept-Language", "zh-CN,zh;q=0.8");
                    var deviceRes = http.GetHtml(item1);

                    //  模拟JS生成相关参数进行下一次请求获取CacheDeviceID
                    var cof = JsonConvert.DeserializeObject<Dictionary<string, string>>(RegGetStr(deviceRes.Html, "Config=", ";"));
                    var c = "{\"na\":\"N/A\",\"deviceCode\":\"7af746974dc2482cd562d21549e4b6b659b1bc12;8c67f4af3a2362fedc24b0f3997eb900\",\"appVersion\":\"5.0 (Windows NT 6.3; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/52.0.2743.116 Safari/537.36\",\"timeZone\":\"-480\",\"userAgent\":\"Mozilla/5.0 (Windows NT 6.3; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/52.0.2743.116 Safari/537.36\",\"screen\":{\"width\":1920,\"height\":1080,\"colorDepth\":24},\"deviceId\":\"" + cof["defaultDeviceId"] + "\",\"href\":\"" + loginParam.WebUrl + "/Default.aspx\",\"capturedDate\":\"" + cof["capturedDate"] + "\"}";
                    var _di = Convert.ToBase64String(Encoding.UTF8.GetBytes(c));

                    //首页访问成功
                    HtmlDocument htmlDoc = new HtmlDocument();
                    htmlDoc.LoadHtml(result.Html);
                    //获取验证码
                    HtmlNode node = htmlDoc.GetElementbyId("txtCode");
                    string code = "";
                    if (node != null) code = node.Attributes["value"].Value;
                    item.URL = loginParam.WebUrl + "/getBeforeLoginCode.ashx?_=" + ToUnixTimeSpan(DateTime.Now);
                    item.Cookie = mainCookie;
                    var rs = http.GetHtml(item);
                    code = rs.Html;

                    // 获取tk
                    HtmlNode node1 = htmlDoc.GetElementbyId("__tk");
                    string tk = "";
                    if (node1 != null) tk = node1.Attributes["value"].Value;

                    //ibc对会员密码加密
                    string CFSKey = CFS.Encrypt(loginParam.Password);
                    string EnCryptData = CFSKey + code;
                    string hidKey = ML.Security.MD5.Encrypt(EnCryptData);
                    //IBC有一个参数__di又两个加密JS制作 暂时无用所以没加
                    string _data = "txtID=" + loginParam.Username + "&txtPW2=" + loginParam.Password + "&txtPW=" + hidKey +
                                   "&txtCode=" + code +
                                   "&hidKey=&hidServerKey=maxbet.com&hidLowerCasePW=&IEVerison=0&IsSSL=1&PF=Default&__tk=" + tk + "&__di=" + _di;// + "&detecas-analysis=" + dataAnaly;

                    item = new HttpItem()
                    {
                        URL = loginParam.WebUrl + "/ProcessLogin.aspx",
                        Method = "POST",
                        ContentType = "application/x-www-form-urlencoded",
                        Cookie = mainCookie,//_cookieDic.ParseCookieDictionary(),
                        Postdata = _data,
                        UserAgent = userAgent,
                        Accept = "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8",
                        Referer = loginParam.WebUrl + "/Default.aspx?IsSSL=1",
                        KeepAlive = true,
                        Expect100Continue = false,
                        Host = "www.maxbet.com",
                    };
                    item.Header.Add("Accept-Language", "zh-CN,zh;q=0.8");
                    item.Header.Add("Cache-Control", "max-age=0");
                    item.Header.Add("Upgrade-Insecure-Requests", "1");
                    item.KeepAlive = true;
                    result = http.GetHtml(item);
                    mainCookie = HttpCookieHelper.CombineCookie(mainCookie, result.Cookie);

                    //  登录页面访问成功将跳转到子域名验证页面
                    if (result.StatusCode == HttpStatusCode.OK)
                    {
                        #region 新的登录流程

                        if (result.Html.Contains("GetLoginVerifyInfo.aspx"))
                        {
                            var url = RegGetStr(result.Html, "location='.", "'");
                            item = new HttpItem()
                            {
                                URL = loginParam.WebUrl + url,
                                Cookie = mainCookie,
                                UserAgent = userAgent,
                                Accept = accept,
                                Referer = loginParam.WebUrl + "/ProcessLogin.aspx",
                                KeepAlive = true,
                            };
                            item.Header.Add("Accept-Language", "en-US;q=0.5,en;q=0.3");
                            item.Header.Add("Accept-Encoding", acceptEncoding);
                            item.Header.Add("Upgrade-Insecure-Requests", "1");
                            var r = http.GetHtml(item);
                            localUrl = RegGetStr(r.Html, "href=\"", ".com") + ".com";
                            if (!r.RedirectUrl.Contains("ValidateToken"))
                            {
                                logger.Error("Failed ValidateToken! " + "\n Html = " + r.Html);
                                return false;
                            }

                            mainCookie = HttpCookieHelper.RemoveCookieByKey("ASP.NET_SessionId", mainCookie);
                            mainCookie = HttpCookieHelper.RemoveCookieByFuzzyKey("sto-fx", mainCookie);
                            item.URL = r.RedirectUrl;
                            item.Cookie = mainCookie;
                            r = http.GetHtml(item);

                            if (r.Html.Contains("ChangeAccountPassword"))
                            {
                                Console.WriteLine("MessageBox|Need to change the account password on the IBC website.");
                                Thread.Sleep(2 * 60 * 1000);
                                return false;
                            }

                            if (!r.Html.Contains("/sports"))
                            {
                                logger.Error("Failed ValidateToken! " + "\n Html = " + r.Html);
                                return false;
                            }

                            mainCookie = HttpCookieHelper.CombineCookie(mainCookie, r.Cookie);
                            item.URL = localUrl + "/sports";
                            item.Cookie = mainCookie;
                            result = http.GetHtml(item);

                            if (result.StatusCode != HttpStatusCode.OK)
                            {
                                logger.Error("Failed sports! " + "\n Html = " + r.Html);
                                return false;
                            }
                            //mainCookie = HttpCookieHelper.CombineCookie(mainCookie, r.Cookie);
                            //mainCookie = HttpCookieHelper.RemoveCookieByKey("_culture", mainCookie);
                            //item.URL = localUrl + "/SwitchPlatform/SwitchToOtherSite/";
                            //item.Cookie = mainCookie;
                            //item.Referer = localUrl + "/sports";
                            //result = http.GetHtml(item);
                        }
                        #endregion

                        #region 点击Agree
                        //  点击Agree
                        if (result.Html.Contains("rulesalert.aspx"))
                        {
                            item.URL = loginParam.WebUrl + "/rulesalert.aspx";
                            item.Method = "GET";
                            item.Referer = loginParam.WebUrl + "/ProcessLogin.aspx";
                            var res = http.GetHtml(item);

                            item.URL = loginParam.WebUrl + "/rulesContent.aspx";
                            item.Method = "GET";
                            item.Referer = loginParam.WebUrl + "/rulesalert.aspx";
                            res = http.GetHtml(item);

                            item.URL = loginParam.WebUrl + "/rulesalert.aspx";
                            item.Method = "POST";
                            item.Postdata = "Accept=YES";
                            item.Referer = item.URL;
                            item.ContentType = "application/x-www-form-urlencoded";
                            res = http.GetHtml(item);
                        }
                        #endregion

                        if (result.StatusCode == HttpStatusCode.OK)
                        {
                            if (!ValidateFailed(result.Html)) return false;

                            if (!GetNecessaryParam(result.Html, mainCookie + result.Cookie))
                            {
                                logger.Error("Cannot get necessaryParam , html = " + result.Html);
                                return false;
                            }
                            mainCookie += result.Cookie;

                            //  访问另一个子域名获取cookie
                            item.URL = "https://agnj3.maxbet.com/socket.io/?gid=" + GetGid() + "&token=" + SocketInitDto.Token + "&id=" + SocketInitDto.Id + "&rid=" + SocketInitDto.Rid + "&EIO=3&transport=polling";
                            item.Accept = "*/*";
                            item.Host = "agnj3.maxbet.com";
                            item.Referer = item.Referer + "/sports";
                            var anotherRes = http.GetHtml(item);
                            mainCookie = HttpCookieHelper.CombineCookie(mainCookie, anotherRes.Cookie);
                            mainCookie = HttpCookieHelper.RemoveCookieByKey("io", mainCookie);

                            Console.WriteLine("Login Success.");
                            logger.Info("Login Success!");
                            Available = true;
                            Status = TakerStatus.Started;
                            checkCookie = mainCookie;
                            checkUrl = localUrl;
                            loginCheckTimer.Start();
                            return true;
                        }
                    }
                }
                Console.WriteLine("Request web page failed,check network connection or network envirenment");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Login failed," + ex.Message);
                logger.Error(ex.ToString());
                return false;
            }
        }
        private bool ValidateFailed(string html)
        {
            if (html.Contains("Incorrect username/password"))
            {
                Console.WriteLine("MessageBox|Incorrect username/password");
                Thread.Sleep(2 * 60 * 1000);
                return false;
            }
            else if (html.Contains("locked"))
            {
                Console.WriteLine("MessageBox|Account has been locked!");
                Thread.Sleep(2 * 60 * 1000);
                return false;
            }
            else
            {
                var h = html.ToLower();
                if (h.Contains("login too often"))
                {
                    if (h.Contains("5 minutes"))
                    {
                        Console.WriteLine("Login too often,current IP address has been blocked!");
                        Thread.Sleep(60000 + 6 * 60 * 1000);
                        return false;
                    }
                    else
                    {
                        Console.WriteLine("Login too often,will wait 60s and retry.");
                        Thread.Sleep(60000);
                        return false;
                    }
                }
            }

            return true;
        }
        public void LoginTask()
        {
            new Task(() =>
            {
                while (!AutoLogin()) { Thread.Sleep(5000); }
            }).Start();
        }
        private string RegGetStr(string originStr, string startStr, string endStr)
        {
            Regex rg = new Regex("(?<=(" + startStr + "))[.\\s\\S]*?(?=(" + endStr + "))", RegexOptions.Multiline | RegexOptions.Singleline);
            return rg.Match(originStr).Value;
        }
        private bool IndexPageCheck(HttpResult result, HttpItem indexItem, out string mainCookie)
        {
            var count = 0;
            mainCookie = result.Cookie;
            var http = new HttpHelper();
            do
            {
                if (result.Cookie.Contains("nlbi") || result.Cookie.Contains("ASP.NET_SessionId")) return true;

                var ir_url = Get_Incapsula_Resource_URL(result.Html);
                var item2 = new HttpItem()
                {
                    URL = loginParam.WebUrl + ir_url,
                    Cookie = mainCookie
                };
                var r = http.GetHtml(item2);
                if (!string.IsNullOrEmpty(r.Cookie)) mainCookie = HttpCookieHelper.CombineCookie(mainCookie, r.Cookie);
                indexItem.Cookie = mainCookie;
                result = http.GetHtml(indexItem);
                mainCookie = HttpCookieHelper.CombineCookie(mainCookie, result.Cookie);
                if (mainCookie.Contains("nlbi")) return true;
                count++;
                if (count > 3) { logger.Error("Index page Robots check failed over 3 times!!!"); }
            } while (count < 3);

            return false;
        }
        private string Get_Incapsula_Resource_URL(string text)
        {
            var u = "";
            try
            {
                Regex _r = new Regex("var b=\"(.+?)\";");
                Match m = _r.Match(text);
                var b = m.Groups[1].Value;

                string z = "";
                for (var i = 0; i < b.Length; i += 2)
                {
                    z = z + Convert.ToInt32(b.Substring(i, 2), 16) + ",";
                }
                z = z.TrimEnd(',');
                var zs = z.Split(',');
                var s = "";
                foreach (var t in zs) s += Convert.ToChar(int.Parse(t)).ToString();

                _r = new Regex("xhr.open\\(\"GET\",\"(.+?)\",false\\);");
                m = _r.Match(s);
                u = m.Groups[1].Value;
            }
            catch (Exception ex)
            {
                logger.Error(ex.ToString());
                logger.Error(text);
            }
            return u;
        }
        private bool GetNecessaryParam(string html, string cookie)
        {
            HtmlDocument doc = new HtmlDocument();
            doc.LoadHtml(html);
            var node = doc.DocumentNode.SelectSingleNode("/html/head/script[10]/text()");
            if (node == null) return false;

            //拿到主界面的token和id
            Regex regex = new Regex("tk\":\"(.*)\",\"no\":(.*)},.*(?<=ID)\"(.*)\"(?=Name)");
            var mc = regex.Match(html);
            string token = mc.Groups[1].Value;
            string rid = mc.Groups[2].Value;
            string id = mc.Groups[3].Value.Replace(":", "").Replace(",", "");
            SocketInitDto = new SocketInitDto()
            {
                Id = id,
                Rid = rid,
                LocalUrl = localUrl,
                Token = token,
                Cookie = cookie
            };
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(rid) || string.IsNullOrEmpty(id))
            {
                logger.Error("Cannot parse param. html = " + html);
                return false;
            }
            return true;
        }
        private void LoginCheckin(object sender, ElapsedEventArgs ee)
        {
            try
            {
                var cookie = checkCookie;
                var url = checkUrl;
                HttpHelper helper = new HttpHelper();
                HttpItem item = new HttpItem()
                {
                    URL = url + "/LoginCheckin/Index",
                    Method = "POST",
                    ContentType = "application/x-www-form-urlencoded",
                    Accept = "application/json, text/javascript, */*; q=0.01",
                    UserAgent = userAgent,
                    Referer = url + "/sports",
                    Host = Regex.Split(url, "//")[1],
                    Cookie = cookie + "_v1promo=1,Wel_" + loginParam.Username + "_=1,",
                    Expect100Continue = false,
                    Postdata = "username=S688HZZ00S02&key=login",
                };

                item.Header.Add("username", loginParam.Username);
                item.Header.Add("Origin", url);
                item.Header.Add("X-Requested-With", "XMLHttpRequest");
                item.Header.Add("Accept-Language", "en-US;q=0.5,en;q=0.3");
                item.Header.Add("Accept-Encoding", acceptEncoding);

                var r = helper.GetHtml(item);
                var res = JsonConvert.DeserializeObject<dynamic>(r.Html);

                //获取返回的json，解析出需要的ErrorCode，判断当前状态
                var ErrorCode = res["ErrorCode"].ToString();
                //0表示登录状态正常，其他表示异常。
                if (ErrorCode != "0")
                {
                    logger.Error("LoginCheck Failed ErrorCode = " + ErrorCode + "\r\nHtml = " + r.Html);
                    return;
                    loginCheckTimer.Stop();
                    var logout = new HttpItem()
                    {
                        URL = url + "/Logout",
                        Method = "GET",
                        KeepAlive = true,
                        Accept = accept,
                        UserAgent = userAgent,
                        Referer = url + "/sports",
                        Host = Regex.Split(url, "//")[1],
                        Cookie = cookie,
                        Allowautoredirect = true
                    };
                    helper.GetHtml(logout);
                    Thread.Sleep(10 * 1000);
                    if (ForceCloseSocket != null) ForceCloseSocket();
                    return;
                }
                logger.Debug("LoginCheckin cookie = " + cookie);

            }
            catch (Exception e)
            {
                logger.Error(e.ToString());
            }
        }

        private static long ToUnixTimeSpan(DateTime date)
        {
            var startTime = TimeZone.CurrentTimeZone.ToLocalTime(new DateTime(1970, 1, 1)); // 当地时区
            return (long)(date - startTime).TotalMilliseconds; // 相差毫秒数
        }

        private string GetGid()
        {
            Random rd = new Random();
            return u(rd) + u(rd) + u(rd) + u(rd);
        }

        private string u(Random rd)
        {
            return Convert.ToString((int)Math.Floor(65536 * (1 + rd.NextDouble())), 16).Substring(1);
        }
    }
}
