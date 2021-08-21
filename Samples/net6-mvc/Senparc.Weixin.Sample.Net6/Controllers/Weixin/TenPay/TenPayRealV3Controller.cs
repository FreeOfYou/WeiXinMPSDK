﻿/*----------------------------------------------------------------
    Copyright (C) 2021 Senparc
    
    文件名：TenPayController.cs
    文件功能描述：微信支付V3 Controller
    
    
    创建标识：Senparc - 20210820

    修改标识：Senparc - 20210821
    修改描述：加入账单查询/关闭账单Demo

----------------------------------------------------------------*/

using Microsoft.AspNetCore.Mvc;
using Senparc.CO2NET.Extensions;
using Senparc.CO2NET.Utilities;
using Senparc.Weixin.Exceptions;
using Senparc.Weixin.Helpers;
using Senparc.Weixin.MP;
using Senparc.Weixin.MP.AdvancedAPIs;
using Senparc.Weixin.MP.Sample.CommonService.TemplateMessage;
using Senparc.Weixin.Sample.NetCore3.Controllers;
using Senparc.Weixin.Sample.NetCore3.Filters;
using Senparc.Weixin.Sample.NetCore3.Models;
using Senparc.Weixin.TenPayV3;
using Senparc.Weixin.TenPayV3.Apis;
using Senparc.Weixin.TenPayV3.Apis.BasePay;
using Senparc.Weixin.TenPayV3.Apis.BasePay.Entities;
using Senparc.Weixin.TenPayV3.Entities;
using Senparc.Weixin.TenPayV3.Helpers;
using System.Text;

namespace Senparc.Weixin.Sample.Net6.Controllers
{
    public class TenPayRealV3Controller : BaseController
    {
        private static TenPayV3Info _tenPayV3Info;

        public static TenPayV3Info TenPayV3Info
        {
            get
            {
                if (_tenPayV3Info == null)
                {
                    var key = TenPayHelper.GetRegisterKey(Config.SenparcWeixinSetting);

                    _tenPayV3Info =
                        TenPayV3InfoCollection.Data[key];
                }
                return _tenPayV3Info;
            }
        }

        /// <summary>
        /// 获取用户的OpenId
        /// </summary>
        /// <returns></returns>
        public IActionResult Index(int productId = 0, int hc = 0)
        {
            if (productId == 0 && hc == 0)
            {
                return RedirectToAction("ProductList");
            }

            var returnUrl = string.Format("https://sdk.weixin.senparc.com/TenPayV3/JsApi");
            var state = string.Format("{0}|{1}", productId, hc);
            var url = OAuthApi.GetAuthorizeUrl(TenPayV3Info.AppId, returnUrl, state, OAuthScope.snsapi_userinfo);

            return Redirect(url);
        }

        #region 产品展示
        public IActionResult ProductList()
        {
            var products = ProductModel.GetFakeProductList();
            return View(products);
        }

        public ActionResult ProductItem(int productId, int hc)
        {
            var products = ProductModel.GetFakeProductList();
            var product = products.FirstOrDefault(z => z.Id == productId);
            if (product == null || product.GetHashCode() != hc)
            {
                return Content("商品信息不存在，或非法进入！2003");
            }

            //判断是否正在微信端
            if (Senparc.Weixin.BrowserUtility.BrowserUtility.SideInWeixinBrowser(HttpContext))
            {
                //正在微信端，直接跳转到微信支付页面
                return RedirectToAction("JsApi", new { productId = productId, hc = hc });
            }
            else
            {
                //在PC端打开，提供二维码扫描进行支付
                return View(product);
            }
        }
        #endregion

        #region OAuth授权
        public ActionResult OAuthCallback(string code, string state, string returnUrl)
        {
            try
            {
                if (string.IsNullOrEmpty(code))
                {
                    return Content("您拒绝了授权！");
                }

                if (!state.Contains("|"))
                {
                    //这里的state其实是会暴露给客户端的，验证能力很弱，这里只是演示一下
                    //实际上可以存任何想传递的数据，比如用户ID
                    return Content("验证失败！请从正规途径进入！1001");
                }

                //通过，用code换取access_token
                var openIdResult = OAuthApi.GetAccessToken(TenPayV3Info.AppId, TenPayV3Info.AppSecret, code);
                if (openIdResult.errcode != ReturnCode.请求成功)
                {
                    return Content("错误：" + openIdResult.errmsg);
                }

                HttpContext.Session.SetString("OpenId", openIdResult.openid);//进行登录

                //也可以使用FormsAuthentication等其他方法记录登录信息，如：
                //FormsAuthentication.SetAuthCookie(openIdResult.openid,false);

                return Redirect(returnUrl);
            }
            catch (Exception ex)
            {
                return Content(ex.ToString());
            }
        }
        #endregion

        #region JsApi支付

        //需要OAuth登录
        [CustomOAuth(null, "/TenpayV3/OAuthCallback")]
        public async Task<IActionResult> JsApi(int productId, int hc)
        {
            try
            {
                //获取产品信息
                var products = ProductModel.GetFakeProductList();
                var product = products.FirstOrDefault(z => z.Id == productId);
                if (product == null || product.GetHashCode() != hc)
                {
                    return Content("商品信息不存在，或非法进入！1002");
                }

                //var openId = User.Identity.Name;
                var openId = HttpContext.Session.GetString("OpenId");

                string sp_billno = Request.Query["order_no"];
                if (string.IsNullOrEmpty(sp_billno))
                {
                    //生成订单10位序列号，此处用时间和随机数生成，商户根据自己调整，保证唯一
                    sp_billno = string.Format("{0}{1}{2}", TenPayV3Info.MchId/*10位*/, SystemTime.Now.ToString("yyyyMMddHHmmss"),
                        TenPayV3Util.BuildRandomStr(6));

                    //注意：以上订单号仅作为演示使用，如果访问量比较大，建议增加订单流水号的去重检查。
                }
                else
                {
                    sp_billno = Request.Query["order_no"];
                }

                var timeStamp = TenPayV3Util.GetTimestamp();
                var nonceStr = TenPayV3Util.GetNoncestr();


                //调用下单接口下单
                var name = product == null ? "test" : product.Name;
                var price = product == null ? 100 : (int)(product.Price * 100);//单位：分

                //var xmlDataInfo = new TenPayV3UnifiedorderRequestData(TenPayV3Info.AppId, TenPayV3Info.MchId, body, sp_billno, price, HttpContext.UserHostAddress()?.ToString(), TenPayV3Info.TenPayV3Notify, TenPay.TenPayV3Type.JSAPI, openId, TenPayV3Info.Key, nonceStr);
                JsApiRequestData jsApiRequestData = new(new TenpayDateTime(DateTime.Now), new JsApiRequestData.Amount() { currency = "CNY", total = price },
                    TenPayV3Info.MchId, name, TenPayV3Info.TenPayV3Notify, new JsApiRequestData.Payer() { openid = openId }, sp_billno, null, TenPayV3Info.AppId,
                    null, null, null, null);

                //var result = TenPayOldV3.Unifiedorder(xmlDataInfo);//调用统一订单接口
                //JsSdkUiPackage jsPackage = new JsSdkUiPackage(TenPayV3Info.AppId, timeStamp, nonceStr,);
                var result = await BasePayApis.JsApiAsync(jsApiRequestData);
                var package = string.Format("prepay_id={0}", result.prepay_id);

                ViewData["product"] = product;

                ViewData["appId"] = TenPayV3Info.AppId;
                ViewData["timeStamp"] = timeStamp;
                ViewData["nonceStr"] = nonceStr;
                ViewData["package"] = package;
                //ViewData["paySign"] = TenPayOldV3.GetJsPaySign(TenPayV3Info.AppId, timeStamp, nonceStr, package, TenPayV3Info.Key);
                ViewData["paySign"] = TenPaySignHelper.CreatePaySign(timeStamp, nonceStr, package);

                //临时记录订单信息，留给退款申请接口测试使用
                HttpContext.Session.SetString("BillNo", sp_billno);
                HttpContext.Session.SetString("BillFee", price.ToString());

                return View();
            }
            catch (Exception ex)
            {
                var msg = ex.Message;
                msg += "<br>" + ex.StackTrace;
                msg += "<br>==Source==<br>" + ex.Source;

                if (ex.InnerException != null)
                {
                    msg += "<br>===InnerException===<br>" + ex.InnerException.Message;
                }
                return Content(msg);
            }
        }

        /// <summary>
        /// JS-SDK支付回调地址（在统一下单接口中设置notify_url）
        /// </summary>
        /// <returns></returns>
        public IActionResult PayNotifyUrl()
        {
            try
            {
                //ResponseHandler resHandler = new ResponseHandler(HttpContext);
                var resHandler = new TenPayNotifyHandler(HttpContext);
                var orderReturnJson = resHandler.AesGcmDecryptGetObject<OrderReturnJson>();

                //string return_code = resHandler.GetParameter("return_code");
                //string return_msg = resHandler.GetParameter("return_msg");
                string trade_state = orderReturnJson.trade_state;

                string res = null;

                //resHandler.SetKey(TenPayV3Info.Key);
                ////验证请求是否从微信发过来（安全）
                //if (resHandler.IsTenpaySign() && return_code.ToUpper() == "SUCCESS")
                //{
                //    res = "success";//正确的订单处理
                //                    //直到这里，才能认为交易真正成功了，可以进行数据库操作，但是别忘了返回规定格式的消息！
                //}
                //else
                //{
                //    res = "wrong";//错误的订单处理
                //}

                //验证请求是否从微信发过来（安全）
                if (orderReturnJson.Signed == true && trade_state == "SUCCESS")
                {
                    res = "success";//正确的订单处理
                                    //直到这里，才能认为交易真正成功了，可以进行数据库操作，但是别忘了返回规定格式的消息！
                }
                else
                {
                    res = "wrong";//错误的订单处理
                }


                /* 这里可以进行订单处理的逻辑 */

                //发送支付成功的模板消息
                try
                {
                    string appId = Config.SenparcWeixinSetting.TenPayV3_AppId;//与微信公众账号后台的AppId设置保持一致，区分大小写。
                    //string openId = resHandler.GetParameter("openid");
                    string openId = orderReturnJson.payer.openid;
                    //var templateData = new WeixinTemplate_PaySuccess("https://weixin.senparc.com", "购买商品", "状态：" + return_code);
                    var templateData = new WeixinTemplate_PaySuccess("https://weixin.senparc.com", "购买商品", "状态：" + trade_state);

                    Senparc.Weixin.WeixinTrace.SendCustomLog("支付成功模板消息参数", appId + " , " + openId);

                    var result = MP.AdvancedAPIs.TemplateApi.SendTemplateMessage(appId, openId, templateData);
                }
                catch (Exception ex)
                {
                    Senparc.Weixin.WeixinTrace.SendCustomLog("支付成功模板消息", ex.ToString());
                }

                #region 记录日志

                var logDir = ServerUtility.ContentRootMapPath(string.Format("~/App_Data/TenPayNotify/{0}", SystemTime.Now.ToString("yyyyMMdd")));
                if (!Directory.Exists(logDir))
                {
                    Directory.CreateDirectory(logDir);
                }

                var logPath = Path.Combine(logDir, string.Format("{0}-{1}-{2}.txt", SystemTime.Now.ToString("yyyyMMdd"), SystemTime.Now.ToString("HHmmss"), Guid.NewGuid().ToString("n").Substring(0, 8)));

                using (var fileStream = System.IO.File.OpenWrite(logPath))
                {
                    //var notifyXml = resHandler.ParseXML();
                    var notifyJson = orderReturnJson.ToString();
                    //fileStream.Write(Encoding.Default.GetBytes(res), 0, Encoding.Default.GetByteCount(res));
                    fileStream.Write(Encoding.Default.GetBytes(notifyJson), 0, Encoding.Default.GetByteCount(notifyJson));
                    fileStream.Close();
                }
                #endregion

                //        string xml = string.Format(@"<xml>
                //<return_code><![CDATA[{0}]]></return_code>
                //<return_msg><![CDATA[{1}]]></return_msg>
                //</xml>", return_code, return_msg);

                //return Content(xml, "text/xml");

                //TODO: 此处不知是否还需要签名,文档说我们要对微信回调请求验证签名,这里并未说回个OK是否需要签名
                //如果需要,也不知何种方式处理
                //https://pay.weixin.qq.com/wiki/doc/apiv3/wechatpay/wechatpay3_3.shtml
                return Json(new NotifyReturnData(trade_state, trade_state));
            }
            catch (Exception ex)
            {
                WeixinTrace.WeixinExceptionLog(new WeixinException(ex.Message, ex));
                throw;
            }
        }
        #endregion

        #region 订单及退款

        /// <summary>
        /// 退款申请接口
        /// </summary>
        /// <returns></returns>
        public async Task<IActionResult> Refund()
        {
            try
            {
                WeixinTrace.SendCustomLog("进入退款流程", "1");

                string nonceStr = TenPayV3Util.GetNoncestr();

                string outTradeNo = HttpContext.Session.GetString("BillNo");

                WeixinTrace.SendCustomLog("进入退款流程", "2 outTradeNo：" + outTradeNo);

                string outRefundNo = "OutRefunNo-" + SystemTime.Now.Ticks;
                int totalFee = int.Parse(HttpContext.Session.GetString("BillFee"));
                int refundFee = totalFee;
                string opUserId = TenPayV3Info.MchId;
                var notifyUrl = "https://sdk.weixin.senparc.com/TenPayV3/RefundNotifyUrl";
                //var dataInfo = new TenPayV3RefundRequestData(TenPayV3Info.AppId, TenPayV3Info.MchId, TenPayV3Info.Key,
                //    null, nonceStr, null, outTradeNo, outRefundNo, totalFee, refundFee, opUserId, null, notifyUrl: notifyUrl);
                var dataInfo = new RefundRequsetData(outTradeNo, outTradeNo, outRefundNo, "Senparc TenPayV3 demo退款测试", notifyUrl, null, new RefundRequsetData.Amount(refundFee, refundFee, "CNY", null));


                //#region 新方法（Senparc.Weixin v6.4.4+）
                //var result = TenPayOldV3.Refund(_serviceProvider, dataInfo);//证书地址、密码，在配置文件中设置，并在注册微信支付信息时自动记录
                //#endregion
                var result = await BasePayApis.RefundAsync(dataInfo);

                WeixinTrace.SendCustomLog("进入退款流程", "3 Result：" + result.ToJson());
                ViewData["Message"] = $"退款结果：{result.status} {result.ResultCode}。您可以刷新当前页面查看最新结果。";
                return View();
                //return Json(result, JsonRequestBehavior.AllowGet);
            }
            catch (Exception ex)
            {
                WeixinTrace.WeixinExceptionLog(new WeixinException(ex.Message, ex));

                throw;
            }
        }

        /// <summary>
        /// 退款通知地址
        /// </summary>
        /// <returns></returns>
        public IActionResult RefundNotifyUrl()
        {
            WeixinTrace.SendCustomLog("RefundNotifyUrl被访问", "IP" + HttpContext.UserHostAddress()?.ToString());

            string responseCode = "FAIL";
            string responseMsg = "FAIL";
            try
            {
                //ResponseHandler resHandler = new ResponseHandler(null
                var resHandler = new TenPayNotifyHandler(HttpContext);
                var refundNotifyJson = resHandler.AesGcmDecryptGetObject<RefundNotifyJson>();

                //string return_code = resHandler.GetParameter("return_code");
                //string return_msg = resHandler.GetParameter("return_msg");
                string refund_status = refundNotifyJson.refund_status;

                //WeixinTrace.SendCustomLog("跟踪RefundNotifyUrl信息", resHandler.ParseXML());
                WeixinTrace.SendCustomLog("跟踪RefundNotifyUrl信息", refundNotifyJson.ToJson());

                if (refund_status == "SUCCESS")
                {
                    responseCode = "SUCCESS";
                    responseMsg = "OK";

                    //string appId = resHandler.GetParameter("appid");
                    //string mch_id = resHandler.GetParameter("mch_id");
                    //string nonce_str = resHandler.GetParameter("nonce_str");
                    //string req_info = resHandler.GetParameter("req_info");

                    //TODO: 返回的数据中并没有mchid该如何判断?
                    //if (!appId.Equals(Senparc.Weixin.Config.SenparcWeixinSetting.TenPayV3_AppId))
                    //{
                    //    /* 
                    //     * 注意：
                    //     * 这里添加过滤只是因为盛派Demo经常有其他公众号错误地设置了我们的地址，
                    //     * 导致无法正常解密，平常使用不需要过滤！
                    //     */
                    //    SenparcTrace.SendCustomLog("RefundNotifyUrl 的 AppId 不正确",
                    //        $"appId:{appId}\r\nmch_id:{mch_id}\r\nreq_info:{req_info}");
                    //    return Content("faild");
                    //}

                    //var decodeReqInfo = TenPayV3Util.DecodeRefundReqInfo(req_info, TenPayV3Info.Key);
                    //var decodeDoc = XDocument.Parse(decodeReqInfo);

                    //获取接口中需要用到的信息
                    //string transaction_id = decodeDoc.Root.Element("transaction_id").Value;
                    //string out_trade_no = decodeDoc.Root.Element("out_trade_no").Value;
                    //string refund_id = decodeDoc.Root.Element("refund_id").Value;
                    //string out_refund_no = decodeDoc.Root.Element("out_refund_no").Value;
                    //int total_fee = int.Parse(decodeDoc.Root.Element("total_fee").Value);
                    //int? settlement_total_fee = decodeDoc.Root.Element("settlement_total_fee") != null
                    //        ? int.Parse(decodeDoc.Root.Element("settlement_total_fee").Value)
                    //        : null as int?;
                    //int refund_fee = int.Parse(decodeDoc.Root.Element("refund_fee").Value);
                    //int tosettlement_refund_feetal_fee = int.Parse(decodeDoc.Root.Element("settlement_refund_fee").Value);
                    //string refund_status = decodeDoc.Root.Element("refund_status").Value;
                    //string success_time = decodeDoc.Root.Element("success_time").Value;
                    //string refund_recv_accout = decodeDoc.Root.Element("refund_recv_accout").Value;
                    //string refund_account = decodeDoc.Root.Element("refund_account").Value;
                    //string refund_request_source = decodeDoc.Root.Element("refund_request_source").Value;

                    //获取接口中需要用到的信息 例
                    string transaction_id = refundNotifyJson.transaction_id;
                    string out_trade_no = refundNotifyJson.out_trade_no;
                    string refund_id = refundNotifyJson.refund_id;
                    string out_refund_no = refundNotifyJson.out_refund_no;
                    int total_fee = refundNotifyJson.amount.payer_total;
                    int refund_fee = refundNotifyJson.amount.refund;


                    WeixinTrace.SendCustomLog("RefundNotifyUrl被访问", "验证通过");
                }

                //进行后续业务处理
            }
            catch (Exception ex)
            {
                responseMsg = ex.Message;
                WeixinTrace.WeixinExceptionLog(new WeixinException(ex.Message, ex));
            }

            //    string xml = string.Format(@"<xml>
            //<return_code><![CDATA[{0}]]></return_code>
            //<return_msg><![CDATA[{1}]]></return_msg>
            //</xml>", responseCode, responseMsg);
            //    return Content(xml, "text/xml");
            //TODO: 此处不知是否还需要签名,文档说我们要对微信回调请求验证签名,这里并未说回个OK是否需要签名
            //如果需要,也不知何种方式处理
            //https://pay.weixin.qq.com/wiki/doc/apiv3/wechatpay/wechatpay3_3.shtml
            return Json(new NotifyReturnData(responseCode, responseMsg));
        }

        /// <summary>
        /// 订单查询
        /// </summary>
        /// <returns></returns>
        public async Task<IActionResult> OrderQuery(string out_trade_no = null, string transaction_id = null)
        {
            //out_trade_no transaction_id 两个参数不能都为空
            if (out_trade_no is null && transaction_id is null)
            {
                throw new ArgumentNullException(nameof(out_trade_no) + " or " + nameof(transaction_id));
            }

            OrderReturnJson result = null;

            //选择方式查询订单
            if (out_trade_no is not null)
            {
                result = await BasePayApis.OrderQueryByOutTradeNoAsync(out_trade_no, TenPayV3Info.MchId);
            }
            if (transaction_id is not null)
            {
                result = await BasePayApis.OrderQueryByTransactionIdAsync(transaction_id, TenPayV3Info.MchId);
            }

            return Json(result);
        }

        /// <summary>
        /// 关闭订单接口
        /// </summary>
        /// <returns></returns>
        public async Task<IActionResult> CloseOrder(string out_trade_no)
        {

            //out_trade_no transaction_id 两个参数不能都为空
            if (out_trade_no is null)
            {
                throw new ArgumentNullException(nameof(out_trade_no));
            }

            ReturnJsonBase result = null;
            result = await BasePayApis.CloseOrderAsync(out_trade_no, TenPayV3Info.MchId);

            return Json(result);
        }
        #endregion



        //    var result = await BasePayApis.JsApiAsync(jsApiRequestData);
        //    return Json(result);
        //}



        //        public ActionResult BankCode()
        //        {
        //            return View();
        //        }


        //        /// <summary>
        //        /// 原生支付 模式一
        //        /// </summary>
        //        /// <returns></returns>
        //        public ActionResult Native()
        //        {
        //            try
        //            {
        //                RequestHandler nativeHandler = new RequestHandler(null);
        //                string timeStamp = TenPayV3Util.GetTimestamp();
        //                string nonceStr = TenPayV3Util.GetNoncestr();

        //                //商品Id，用户自行定义
        //                string productId = SystemTime.Now.ToString("yyyyMMddHHmmss");

        //                nativeHandler.SetParameter("appid", TenPayV3Info.AppId);
        //                nativeHandler.SetParameter("mch_id", TenPayV3Info.MchId);
        //                nativeHandler.SetParameter("time_stamp", timeStamp);
        //                nativeHandler.SetParameter("nonce_str", nonceStr);
        //                nativeHandler.SetParameter("product_id", productId);
        //                string sign = nativeHandler.CreateMd5Sign("key", TenPayV3Info.Key);

        //                var url = TenPayOldV3.NativePay(TenPayV3Info.AppId, timeStamp, TenPayV3Info.MchId, nonceStr, productId, sign);

        //                BitMatrix bitMatrix;
        //                bitMatrix = new MultiFormatWriter().encode(url, BarcodeFormat.QR_CODE, 600, 600);
        //                var bw = new ZXing.BarcodeWriterPixelData();

        //                var pixelData = bw.Write(bitMatrix);
        //                var bitmap = new System.Drawing.Bitmap(pixelData.Width, pixelData.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);

        //                var fileStream = new MemoryStream();

        //                var bitmapData = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, pixelData.Width, pixelData.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        //                try
        //                {
        //                    // we assume that the row stride of the bitmap is aligned to 4 byte multiplied by the width of the image   
        //                    System.Runtime.InteropServices.Marshal.Copy(pixelData.Pixels, 0, bitmapData.Scan0, pixelData.Pixels.Length);
        //                }
        //                finally
        //                {
        //                    bitmap.UnlockBits(bitmapData);
        //                }
        //                bitmap.Save(_fileStream, System.Drawing.Imaging.ImageFormat.Png);
        //                _fileStream.Seek(0, SeekOrigin.Begin);

        //                return File(_fileStream, "image/png");
        //            }
        //            catch (Exception ex)
        //            {
        //                SenparcTrace.SendCustomLog("TenPayV3.Native 执行出错", ex.Message);
        //                SenparcTrace.BaseExceptionLog(ex);

        //                throw;
        //            }

        //        }

        //        public ActionResult NativeNotifyUrl()
        //        {
        //            ResponseHandler resHandler = new ResponseHandler(null);

        //            //返回给微信的请求
        //            RequestHandler res = new RequestHandler(null);

        //            string openId = resHandler.GetParameter("openid");
        //            string productId = resHandler.GetParameter("product_id");

        //            if (openId == null || productId == null)
        //            {
        //                res.SetParameter("return_code", "FAIL");
        //                res.SetParameter("return_msg", "回调数据异常");
        //            }

        //            //创建支付应答对象
        //            //RequestHandler packageReqHandler = new RequestHandler(null);

        //            var sp_billno = SystemTime.Now.ToString("HHmmss") + TenPayV3Util.BuildRandomStr(26);//最多32位
        //            var nonceStr = TenPayV3Util.GetNoncestr();

        //            //创建请求统一订单接口参数
        //            //packageReqHandler.SetParameter("appid", TenPayV3Info.AppId);
        //            //packageReqHandler.SetParameter("mch_id", TenPayV3Info.MchId);
        //            //packageReqHandler.SetParameter("nonce_str", nonceStr);
        //            //packageReqHandler.SetParameter("body", "test");
        //            //packageReqHandler.SetParameter("out_trade_no", sp_billno);
        //            //packageReqHandler.SetParameter("total_fee", "1");
        //            //packageReqHandler.SetParameter("spbill_create_ip", HttpContext.UserHostAddress()?.ToString());
        //            //packageReqHandler.SetParameter("notify_url", TenPayV3Info.TenPayV3Notify);
        //            //packageReqHandler.SetParameter("trade_type", TenPayV3Type.NATIVE.ToString());
        //            //packageReqHandler.SetParameter("openid", openId);
        //            //packageReqHandler.SetParameter("product_id", productId);

        //            //string sign = packageReqHandler.CreateMd5Sign("key", TenPayV3Info.Key);
        //            //packageReqHandler.SetParameter("sign", sign);

        //            //string data = packageReqHandler.ParseXML();

        //            var xmlDataInfo = new TenPayV3UnifiedorderRequestData(TenPayV3Info.AppId, TenPayV3Info.MchId, "test", sp_billno, 1, HttpContext.UserHostAddress()?.ToString(), TenPayV3Info.TenPayV3Notify, TenPay.TenPayV3Type.JSAPI, openId, TenPayV3Info.Key, nonceStr);


        //            try
        //            {
        //                //调用统一订单接口
        //                var result = TenPayOldV3.Unifiedorder(xmlDataInfo);
        //                //var unifiedorderRes = XDocument.Parse(result);
        //                //string prepayId = unifiedorderRes.Element("xml").Element("prepay_id").Value;

        //                //创建应答信息返回给微信
        //                res.SetParameter("return_code", result.return_code);
        //                res.SetParameter("return_msg", result.return_msg ?? "OK");
        //                res.SetParameter("appid", result.appid);
        //                res.SetParameter("mch_id", result.mch_id);
        //                res.SetParameter("nonce_str", result.nonce_str);
        //                res.SetParameter("prepay_id", result.prepay_id);
        //                res.SetParameter("result_code", result.result_code);
        //                res.SetParameter("err_code_des", "OK");

        //                string nativeReqSign = res.CreateMd5Sign("key", TenPayV3Info.Key);
        //                res.SetParameter("sign", nativeReqSign);
        //            }
        //            catch (Exception)
        //            {
        //                res.SetParameter("return_code", "FAIL");
        //                res.SetParameter("return_msg", "统一下单失败");
        //            }

        //            return Content(res.ParseXML());
        //        }

        //        /// <summary>
        //        /// 原生支付 模式二
        //        /// 根据统一订单返回的code_url生成支付二维码。该模式链接较短，生成的二维码打印到结账小票上的识别率较高。
        //        /// 注意：code_url有效期为2小时，过期后扫码不能再发起支付
        //        /// </summary>
        //        /// <returns></returns>
        //        public ActionResult NativeByCodeUrl()
        //        {
        //            //创建支付应答对象
        //            //RequestHandler packageReqHandler = new RequestHandler(null);

        //            var sp_billno = SystemTime.Now.ToString("HHmmss") + TenPayV3Util.BuildRandomStr(26);
        //            var nonceStr = TenPayV3Util.GetNoncestr();

        //            //商品Id，用户自行定义
        //            string productId = SystemTime.Now.ToString("yyyyMMddHHmmss");

        //            //创建请求统一订单接口参数
        //            //packageReqHandler.SetParameter("appid", TenPayV3Info.AppId);
        //            //packageReqHandler.SetParameter("mch_id", TenPayV3Info.MchId);
        //            //packageReqHandler.SetParameter("nonce_str", nonceStr);
        //            //packageReqHandler.SetParameter("body", "test");
        //            //packageReqHandler.SetParameter("out_trade_no", sp_billno);
        //            //packageReqHandler.SetParameter("total_fee", "1");
        //            //packageReqHandler.SetParameter("spbill_create_ip", HttpContext.UserHostAddress()?.ToString());
        //            //packageReqHandler.SetParameter("notify_url", TenPayV3Info.TenPayV3Notify);
        //            //packageReqHandler.SetParameter("trade_type", TenPayV3Type.NATIVE.ToString());
        //            //packageReqHandler.SetParameter("product_id", productId);

        //            //string sign = packageReqHandler.CreateMd5Sign("key", TenPayV3Info.Key);
        //            //packageReqHandler.SetParameter("sign", sign);

        //            //string data = packageReqHandler.ParseXML();
        //            var xmlDataInfo = new TenPayV3UnifiedorderRequestData(TenPayV3Info.AppId,
        //            TenPayV3Info.MchId,
        //            "test",
        //            sp_billno,
        //            1,
        //            HttpContext.UserHostAddress()?.ToString(),
        //            TenPayV3Info.TenPayV3Notify,
        //            TenPay.TenPayV3Type.NATIVE,
        //            null,
        //            TenPayV3Info.Key,
        //            nonceStr,
        //            productId: productId);
        //            //调用统一订单接口
        //            var result = TenPayOldV3.Unifiedorder(xmlDataInfo);
        //            //var unifiedorderRes = XDocument.Parse(result);
        //            //string codeUrl = unifiedorderRes.Element("xml").Element("code_url").Value;
        //            string codeUrl = result.code_url;
        //            BitMatrix bitMatrix;
        //            bitMatrix = new MultiFormatWriter().encode(codeUrl, BarcodeFormat.QR_CODE, 600, 600);
        //            var bw = new ZXing.BarcodeWriterPixelData();

        //            var pixelData = bw.Write(bitMatrix);
        //            using (var bitmap = new System.Drawing.Bitmap(pixelData.Width, pixelData.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb))
        //            {
        //                using (var ms = new MemoryStream())
        //                {
        //                    var bitmapData = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, pixelData.Width, pixelData.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        //                    try
        //                    {
        //                        // we assume that the row stride of the bitmap is aligned to 4 byte multiplied by the width of the image   
        //                        System.Runtime.InteropServices.Marshal.Copy(pixelData.Pixels, 0, bitmapData.Scan0, pixelData.Pixels.Length);
        //                    }
        //                    finally
        //                    {
        //                        bitmap.UnlockBits(bitmapData);
        //                    }
        //                    bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);

        //                    return File(ms, "image/png");
        //                }
        //            }
        //        }

        //        /// <summary>
        //        /// 刷卡支付
        //        /// </summary>
        //        /// <param name="authCode">扫码设备获取到的微信用户刷卡授权码</param>
        //        /// <returns></returns>
        //        public ActionResult MicroPay(string authCode)
        //        {
        //            RequestHandler payHandler = new RequestHandler(null);

        //            var sp_billno = SystemTime.Now.ToString("HHmmss") + TenPayV3Util.BuildRandomStr(28);
        //            var nonceStr = TenPayV3Util.GetNoncestr();

        //            payHandler.SetParameter("auth_code", authCode);//授权码
        //            payHandler.SetParameter("body", "test");//商品描述
        //            payHandler.SetParameter("total_fee", "1");//总金额
        //            payHandler.SetParameter("out_trade_no", sp_billno);//产生随机的商户订单号
        //            payHandler.SetParameter("spbill_create_ip", HttpContext.UserHostAddress()?.ToString());//终端ip
        //            payHandler.SetParameter("appid", TenPayV3Info.AppId);//公众账号ID
        //            payHandler.SetParameter("mch_id", TenPayV3Info.MchId);//商户号
        //            payHandler.SetParameter("nonce_str", nonceStr);//随机字符串

        //            string sign = payHandler.CreateMd5Sign("key", TenPayV3Info.Key);
        //            payHandler.SetParameter("sign", sign);//签名

        //            var result = TenPayOldV3.MicroPay(payHandler.ParseXML());

        //            //此处只是完成最简单的支付功能，实际情况还需要考虑各种出错的情况，并处理错误，最后返回结果通知用户。

        //            return Content(result);
        //        }


        //        #endregion

        //        #region 订单及退款






        //        #endregion

        //        #region 对账单

        //        /// <summary>
        //        /// 下载对账单
        //        /// </summary>
        //        /// <param name="date">日期，格式如：20170716</param>
        //        /// <returns></returns>
        //        public ActionResult DownloadBill(string date)
        //        {
        //            if (!Request.IsLocal())
        //            {
        //                return Content("出于安全考虑，此操作限定在本机上操作（实际项目可以添加登录权限限制后远程操作）！");
        //            }

        //            string nonceStr = TenPayV3Util.GetNoncestr();
        //            TenPayV3DownloadBillRequestData data = new TenPayV3DownloadBillRequestData(TenPayV3Info.AppId, TenPayV3Info.MchId, nonceStr, null, date, "ALL", TenPayV3Info.Key, null);
        //            var result = TenPayOldV3.DownloadBill(data);
        //            return Content(result);
        //        }

        //        #endregion

        //        #region 红包

        //        /// <summary>
        //        /// 目前支持向指定微信用户的openid发放指定金额红包
        //        /// 注意total_amount、min_value、max_value值相同
        //        /// total_num=1固定
        //        /// 单个红包金额介于[1.00元，200.00元]之间
        //        /// </summary>
        //        /// <returns></returns>
        //        public ActionResult SendRedPack()
        //        {
        //            string nonceStr;//随机字符串
        //            string paySign;//签名
        //            var sendNormalRedPackResult = RedPackApi.SendNormalRedPack(
        //                TenPayV3Info.AppId, TenPayV3Info.MchId, TenPayV3Info.Key,
        //                @"F:\apiclient_cert.p12",     //证书物理地址
        //                "接受收红包的用户的openId",   //接受收红包的用户的openId
        //                "红包发送者名称",             //红包发送者名称
        //                HttpContext.UserHostAddress()?.ToString(),      //IP
        //                100,                          //付款金额，单位分
        //                "红包祝福语",                 //红包祝福语
        //                "活动名称",                   //活动名称
        //                "备注信息",                   //备注信息
        //                out nonceStr,
        //                out paySign,
        //                null,                         //场景id（非必填）
        //                null,                         //活动信息（非必填）
        //                null                          //资金授权商户号，服务商替特约商户发放时使用（非必填）
        //                );

        //            return Content(SerializerHelper.GetJsonString(sendNormalRedPackResult));
        //        }
        //        #endregion

        //        #region 裂变红包

        //        /// <summary>
        //        /// 目前支持向指定微信用户的openid发放指定金额红包
        //        /// 注意total_amount、min_value、max_value值相同
        //        /// total_num=1固定
        //        /// 单个红包金额介于[1.00元，200.00元]之间
        //        /// </summary>
        //        /// <returns></returns>
        //        public ActionResult SendGroupRedPack()
        //        {
        //            string mchbillno = SystemTime.Now.ToString("HHmmss") + TenPayV3Util.BuildRandomStr(28);

        //            string nonceStr = TenPayV3Util.GetNoncestr();
        //            RequestHandler packageReqHandler = new RequestHandler(null);

        //            packageReqHandler.SetParameter("nonce_str", nonceStr);              //随机字符串
        //            packageReqHandler.SetParameter("wxappid", TenPayV3Info.AppId);		  //公众账号ID
        //            packageReqHandler.SetParameter("mch_id", TenPayV3Info.MchId);		  //商户号
        //            packageReqHandler.SetParameter("mch_billno", mchbillno);                 //填入商家订单号
        //            packageReqHandler.SetParameter("send_name", "商户名称");                 //红包发送者名称
        //            packageReqHandler.SetParameter("re_openid", "接受收红包的用户的openId");                 //接受收红包的用户的openId
        //            packageReqHandler.SetParameter("total_amount", "300");                //付款金额，单位分
        //            packageReqHandler.SetParameter("total_num", "3");               //红包发放总人数  必须介于(包括)3到20之间
        //            packageReqHandler.SetParameter("wishing", "红包祝福语");               //红包祝福语
        //            packageReqHandler.SetParameter("amt_type", "ALL_RAND");               //红包金额设置方式ALL_RAND—全部随机,商户指定总金额和红包发放总人数，由微信支付随机计算出各红包金额
        //            //packageReqHandler.SetParameter("amt_list", "各红包具体金额");               //各红包具体金额，自定义金额时必须设置，单位分  示例值"200|100|100"
        //            packageReqHandler.SetParameter("act_name", "活动名称");   //活动名称
        //            packageReqHandler.SetParameter("remark", "备注信息");   //备注信息
        //            string sign = packageReqHandler.CreateMd5Sign("key", TenPayV3Info.Key);
        //            packageReqHandler.SetParameter("sign", sign);	                    //签名
        //            //发红包需要post的数据
        //            string data = packageReqHandler.ParseXML();

        //            //发红包接口地址
        //            string url = "https://api.mch.weixin.qq.com/mmpaymkttransfers/sendgroupredpack";
        //            //本地或者服务器的证书位置（证书在微信支付申请成功发来的通知邮件中）
        //            string cert = @"F:\apiclient_cert.p12";
        //            //私钥（在安装证书时设置）
        //            string password = "";

        //            //ServicePointManager.ServerCertificateValidationCallback = new RemoteCertificateValidationCallback(CheckValidationResult);
        //            //调用证书
        //            X509Certificate2 cer = new X509Certificate2(cert, password, X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.MachineKeySet);

        //            #region 发起post请求
        //            HttpWebRequest webrequest = (HttpWebRequest)HttpWebRequest.Create(url);
        //            webrequest.ClientCertificates.Add(cer);
        //            webrequest.Method = "post";

        //            byte[] postdatabyte = Encoding.UTF8.GetBytes(data);
        //            webrequest.ContentLength = postdatabyte.Length;
        //            Stream stream;
        //            stream = webrequest.GetRequestStream();
        //            stream.Write(postdatabyte, 0, postdatabyte.Length);
        //            stream.Close();

        //            HttpWebResponse httpWebResponse = (HttpWebResponse)webrequest.GetResponse();
        //            StreamReader streamReader = new StreamReader(httpWebResponse.GetResponseStream());
        //            string responseContent = streamReader.ReadToEnd();
        //            #endregion

        //            return Content(responseContent);
        //        }
        //        #endregion

        //        #region 红包查询接口

        //        public ActionResult GetHBInfo(string mchbillno)
        //        {
        //            string nonceStr = TenPayV3Util.GetNoncestr();
        //            RequestHandler packageReqHandler = new RequestHandler(null);

        //            packageReqHandler.SetParameter("nonce_str", nonceStr);              //随机字符串
        //            packageReqHandler.SetParameter("appid", TenPayV3Info.AppId);		  //公众账号ID
        //            packageReqHandler.SetParameter("mch_id", TenPayV3Info.MchId);		  //商户号
        //            packageReqHandler.SetParameter("mch_billno", mchbillno);                 //填入商家订单号
        //            packageReqHandler.SetParameter("bill_type", "MCHT");                 //红包发送者名称
        //            string sign = packageReqHandler.CreateMd5Sign("key", TenPayV3Info.Key);
        //            packageReqHandler.SetParameter("sign", sign);	                    //签名
        //            //发红包需要post的数据
        //            string data = packageReqHandler.ParseXML();

        //            //发红包接口地址
        //            string url = "https://api.mch.weixin.qq.com/mmpaymkttransfers/gethbinfo";
        //            //本地或者服务器的证书位置（证书在微信支付申请成功发来的通知邮件中）
        //            string cert = @"F:\apiclient_cert.p12";
        //            //私钥（在安装证书时设置）
        //            string password = "";
        //            //ServicePointManager.ServerCertificateValidationCallback = new RemoteCertificateValidationCallback(CheckValidationResult);
        //            //调用证书
        //            X509Certificate2 cer = new X509Certificate2(cert, password, X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.MachineKeySet);

        //            #region 发起post请求
        //            HttpWebRequest webrequest = (HttpWebRequest)HttpWebRequest.Create(url);
        //            webrequest.ClientCertificates.Add(cer);
        //            webrequest.Method = "post";

        //            byte[] postdatabyte = Encoding.UTF8.GetBytes(data);
        //            webrequest.ContentLength = postdatabyte.Length;
        //            Stream stream;
        //            stream = webrequest.GetRequestStream();
        //            stream.Write(postdatabyte, 0, postdatabyte.Length);
        //            stream.Close();

        //            HttpWebResponse httpWebResponse = (HttpWebResponse)webrequest.GetResponse();
        //            StreamReader streamReader = new StreamReader(httpWebResponse.GetResponseStream());
        //            string responseContent = streamReader.ReadToEnd();
        //            #endregion

        //            return Content(responseContent);
        //        }

        //        #endregion



        //        private Stream _fileStream = null;

        //        /// <summary>
        //        /// 显示二维码
        //        /// </summary>
        //        /// <param name="productId"></param>
        //        /// <returns></returns>
        //        public ActionResult ProductPayCode(int productId, int hc)
        //        {
        //            var products = ProductModel.GetFakeProductList();
        //            var product = products.FirstOrDefault(z => z.Id == productId);
        //            if (product == null || product.GetHashCode() != hc)
        //            {
        //                return Content("商品信息不存在，或非法进入！2004");
        //            }

        //            var url = string.Format("http://sdk.weixin.senparc.com/TenPayV3/JsApi?productId={0}&hc={1}&t={2}", productId,
        //                product.GetHashCode(), SystemTime.Now.Ticks);

        //            BitMatrix bitMatrix;
        //            bitMatrix = new MultiFormatWriter().encode(url, BarcodeFormat.QR_CODE, 600, 600);
        //            var bw = new ZXing.BarcodeWriterPixelData();

        //            var pixelData = bw.Write(bitMatrix);
        //            var bitmap = new System.Drawing.Bitmap(pixelData.Width, pixelData.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        //            var fileStream = new MemoryStream();
        //            var bitmapData = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, pixelData.Width, pixelData.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        //            try
        //            {
        //                // we assume that the row stride of the bitmap is aligned to 4 byte multiplied by the width of the image   
        //                System.Runtime.InteropServices.Marshal.Copy(pixelData.Pixels, 0, bitmapData.Scan0, pixelData.Pixels.Length);
        //            }
        //            finally
        //            {
        //                bitmap.UnlockBits(bitmapData);
        //            }

        //            fileStream.Flush();//.net core 必须要加
        //            fileStream.Position = 0;//.net core 必须要加

        //            bitmap.Save(fileStream, System.Drawing.Imaging.ImageFormat.Png);

        //            fileStream.Seek(0, SeekOrigin.Begin);
        //            return File(fileStream, "image/png");
        //        }


        //        #endregion

        //        #region H5支付

        //        /// <summary>
        //        /// H5支付
        //        /// </summary>
        //        /// <param name="productId"></param>
        //        /// <param name="hc"></param>
        //        /// <returns></returns>
        //        public ActionResult H5Pay(int productId, int hc)
        //        {
        //            {
        //                try
        //                {
        //                    //获取产品信息
        //                    var products = ProductModel.GetFakeProductList();
        //                    var product = products.FirstOrDefault(z => z.Id == productId);
        //                    if (product == null || product.GetHashCode() != hc)
        //                    {
        //                        return Content("商品信息不存在，或非法进入！1002");
        //                    }

        //                    string openId = null;//此时在外部浏览器，无法或得到OpenId

        //                    string sp_billno = Request.Query["order_no"];
        //                    if (string.IsNullOrEmpty(sp_billno))
        //                    {
        //                        //生成订单10位序列号，此处用时间和随机数生成，商户根据自己调整，保证唯一
        //                        sp_billno = string.Format("{0}{1}{2}", TenPayV3Info.MchId/*10位*/, SystemTime.Now.ToString("yyyyMMddHHmmss"),
        //                            TenPayV3Util.BuildRandomStr(6));
        //                    }
        //                    else
        //                    {
        //                        sp_billno = Request.Query["order_no"];
        //                    }

        //                    var timeStamp = TenPayV3Util.GetTimestamp();
        //                    var nonceStr = TenPayV3Util.GetNoncestr();

        //                    var body = product == null ? "test" : product.Name;
        //                    var price = product == null ? 100 : (int)(product.Price * 100);
        //                    //var ip = Request.Params["REMOTE_ADDR"];
        //                    var xmlDataInfo = new TenPayV3UnifiedorderRequestData(TenPayV3Info.AppId, TenPayV3Info.MchId, body, sp_billno, price, HttpContext.UserHostAddress()?.ToString(), TenPayV3Info.TenPayV3Notify, TenPay.TenPayV3Type.MWEB/*此处无论传什么，方法内部都会强制变为MWEB*/, openId, TenPayV3Info.Key, nonceStr);

        //                    SenparcTrace.SendCustomLog("H5Pay接口请求", xmlDataInfo.ToJson());

        //                    var result = TenPayOldV3.Html5Order(xmlDataInfo);//调用统一订单接口
        //                                                                     //JsSdkUiPackage jsPackage = new JsSdkUiPackage(TenPayV3Info.AppId, timeStamp, nonceStr,);

        //                    /*
        //                     * result:{"device_info":"","trade_type":"MWEB","prepay_id":"wx20170810143223420ae5b0dd0537136306","code_url":"","mweb_url":"https://wx.tenpay.com/cgi-bin/mmpayweb-bin/checkmweb?prepay_id=wx20170810143223420ae5b0dd0537136306\u0026package=1505175207","appid":"wx669ef95216eef885","mch_id":"1241385402","sub_appid":"","sub_mch_id":"","nonce_str":"juTchIZyhXvZ2Rfy","sign":"5A37D55A897C854F64CCCC4C94CDAFE3","result_code":"SUCCESS","err_code":"","err_code_des":"","return_code":"SUCCESS","return_msg":null}
        //                     */
        //                    //return Json(result, JsonRequestBehavior.AllowGet);

        //                    SenparcTrace.SendCustomLog("H5Pay接口返回", result.ToJson());

        //                    var package = string.Format("prepay_id={0}", result.prepay_id);

        //                    ViewData["product"] = product;

        //                    ViewData["appId"] = TenPayV3Info.AppId;
        //                    ViewData["timeStamp"] = timeStamp;
        //                    ViewData["nonceStr"] = nonceStr;
        //                    ViewData["package"] = package;
        //                    ViewData["paySign"] = TenPayOldV3.GetJsPaySign(TenPayV3Info.AppId, timeStamp, nonceStr, package, TenPayV3Info.Key);

        //                    //设置成功页面（也可以不设置，支付成功后默认返回来源地址）
        //                    var returnUrl =
        //                        string.Format("https://sdk.weixin.senparc.com/TenpayV3/H5PaySuccess?productId={0}&hc={1}",
        //                            productId, hc);

        //                    var mwebUrl = result.mweb_url;
        //                    if (!string.IsNullOrEmpty(mwebUrl))
        //                    {
        //                        mwebUrl += string.Format("&redirect_url={0}", returnUrl.AsUrlData());
        //                    }

        //                    ViewData["MWebUrl"] = mwebUrl;

        //                    //临时记录订单信息，留给退款申请接口测试使用
        //                    HttpContext.Session.SetString("BillNo", sp_billno);
        //                    HttpContext.Session.SetString("BillFee", price.ToString());

        //                    return View();
        //                }
        //                catch (Exception ex)
        //                {
        //                    var msg = ex.Message;
        //                    msg += "<br>" + ex.StackTrace;
        //                    msg += "<br>==Source==<br>" + ex.Source;

        //                    if (ex.InnerException != null)
        //                    {
        //                        msg += "<br>===InnerException===<br>" + ex.InnerException.Message;
        //                    }
        //                    return Content(msg);
        //                }
        //            }
        //        }

        //        public ActionResult H5PaySuccess(int productId, int hc)
        //        {
        //            try
        //            {
        //                //TODO：这里可以校验支付是否真的已经成功

        //                //获取产品信息
        //                var products = ProductModel.GetFakeProductList();
        //                var product = products.FirstOrDefault(z => z.Id == productId);
        //                if (product == null || product.GetHashCode() != hc)
        //                {
        //                    return Content("商品信息不存在，或非法进入！1002");
        //                }
        //                ViewData["product"] = product;

        //                return View();
        //            }
        //            catch (Exception ex)
        //            {
        //                var msg = ex.Message;
        //                msg += "<br>" + ex.StackTrace;
        //                msg += "<br>==Source==<br>" + ex.Source;

        //                if (ex.InnerException != null)
        //                {
        //                    msg += "<br>===InnerException===<br>" + ex.InnerException.Message;
        //                }
        //                return Content(msg);
        //            }
        //        }


        //        #endregion

        //        #region 付款到银行卡

        //        //TODO：完善

        //        #endregion
    }
}
