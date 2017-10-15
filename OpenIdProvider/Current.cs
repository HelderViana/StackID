﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using OpenIdProvider.Models;
using OpenIdProvider.Helpers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Net.Sockets;
using System.Net;
using System.Web.Caching;
using System.IO;
using System.Globalization;
using System.Web.Configuration;
using System.Web.Mvc;
using System.Reflection;
using BookSleeve;
using ProtoBuf;
using MvcMiniProfiler;
using MvcMiniProfiler.Data;
using System.Data.SqlClient;
using System.Data.Common;

namespace OpenIdProvider
{
    /// <summary>
    /// Convenience class for accessing information about the current request.
    /// </summary>
    public static class Current
    {
        private static string _readConnectionString;
        /// <summary>
        /// Connection string with read-only access to the DB
        /// </summary>
        private static string ReadConnectionString
        {
            get
            {
                if(_readConnectionString == null)
                    _readConnectionString = WebConfigurationManager.ConnectionStrings["ReadConnectionString"].ConnectionString;

                return _readConnectionString;
            }
        }

        private static string _writeConnectionString;
        /// <summary>
        /// Connection string that allows write access to the DB
        /// </summary>
        private static string WriteConnectionString
        {
            get
            {
                if (_writeConnectionString == null)
                    _writeConnectionString = WebConfigurationManager.ConnectionStrings["WriteConnectionString"].ConnectionString;

                return _writeConnectionString;
            }
        }

        /// <summary>
        /// DB to store into if we're using Redis as a caching layer.
        /// </summary>
        private static Lazy<int?> RedisDB = new Lazy<int?>(
            delegate
            {
                var db = WebConfigurationManager.AppSettings["RedisDB"];

                if (db == null) return null;

                return Int32.Parse(db);
            });

        private static object _redisLock = new object();
        private static RedisConnection _redisConnection;
        /// <summary>
        /// If we're setup to use Redis as a caching layer, return a connection to it.
        /// 
        /// Otherwise, returns null.
        /// </summary>
        private static RedisConnection Redis
        {
            get
            {
                if (_redisConnection != null) return _redisConnection;

                lock (_redisLock)
                {
                    if (_redisConnection != null) return _redisConnection;

                    var server = WebConfigurationManager.AppSettings["RedisServerAddress"];

                    if (server == null) return null;

                    int i = server.IndexOf(':');
                    if (i == -1) throw new Exception("Misconfigured RedisServerAddress, expected [host]:[port]");

                    var host = server.Substring(0, i);
                    var port = Int32.Parse(server.Substring(i + 1));

                    _redisConnection = new RedisConnection(host, port);
                    _redisConnection.Open();
                }

                _redisConnection.Closed += delegate { _redisConnection = null; };
                _redisConnection.Error +=
                    delegate
                    {
                        try
                        {
                            // Suspect connection, bail and re-establish
                            _redisConnection.Close(false);
                        }
                        catch { }
                    };

                return _redisConnection;
            }
        }

        /// <summary>
        /// Name of this site, as configured in web.config
        /// </summary>
        public static string SiteName
        {
            get
            {
                return WebConfigurationManager.AppSettings["SiteName"];
            }
        }

        /// <summary>
        /// Path to the key store file for this OpenIdProvider.
        /// </summary>
        public static string KeyStorePath
        {
            get
            {
                var k = WebConfigurationManager.AppSettings["KeyStore"];
                // my own correctio for relative paths:
                if (k.Contains("~"))
                {
                    string serverPath;
                    serverPath = HttpContext.Current.Server.MapPath(k);
                    k = serverPath.ToString();
                }
                return k;
            }
        }

        /// <summary>
        /// Path to log errors to.
        /// </summary>
        public static string ErrorLogPath
        {
            get
            {
                string path = @"~\Error\";

                try
                {
                    path = WebConfigurationManager.AppSettings["ErrorPath"];
                }
                catch (Exception) { }

                if (!path.EndsWith("\\")) path += "\\";

                // Share path
                if (path.StartsWith(@"\\")) return path;

                return HttpContext.Current.Server.MapPath(path);
            }
        }

        /// <summary>
        /// The IP of any SSL accelerator/load-balancer we're running behind.
        /// 
        /// If set (and not running as DEBUG) we can *only* accept HTTP requests
        /// from this IP.  Everything else must be over HTTPS.
        /// </summary>
        public static string[] LoadBalancerIPs
        {
            get
            {
                var knownIps = WebConfigurationManager.AppSettings["LoadBalancerIP"];

                if (knownIps.IsNullOrEmpty()) return new string[0];

                return knownIps.Split(';').Select(s => s.Trim()).ToArray();
            }
        }

        private static Lazy<Email> EmailCached = new Lazy<Email>(InitEmail);
        /// <summary>
        /// Common accessor for an email instance.
        /// </summary>
        public static Email Email
        {
            get 
            {
                return EmailCached.Value;
            }
        }

        private static List<int> TrustedAffiliateIdsCached;
        /// <summary>
        /// The affiliate ids that are trusted to handle username/password combinations.
        /// 
        /// For use when affiliate forms aren't acceptable, but requires a proper security
        /// audit of the affiliate.
        /// </summary>
        public static IEnumerable<int> TrustedAffiliateIds
        {
            get
            {
                if (TrustedAffiliateIdsCached == null)
                {
                    TrustedAffiliateIdsCached = WebConfigurationManager.AppSettings["TrustedAffiliateIds"].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => int.Parse(s)).ToList();
                }

                return TrustedAffiliateIdsCached;
            }
        }

        /// <summary>
        /// Initialize an Email instance.
        /// This provides the value (via Lazy) for EmailCached.
        /// </summary>
        private static Email InitEmail()
        {
            var emailImpls =
                from t in Assembly.GetExecutingAssembly().GetTypes()
                where t.IsSubclassOf(typeof(Email)) && !t.IsAbstract
                select t;

            var email =
                emailImpls.OrderByDescending(
                    e =>
                    {
                        var priority = e.GetCustomAttributes(typeof(PriorityAttribute), false);

                        if (priority.Length == 0) return Int32.MinValue;

                        return ((PriorityAttribute)priority.ElementAt(0)).Priority;
                    }
                ).First();

            var constructor = email.GetConstructor(new Type[0]);

            if (constructor == null) throw new Exception("No parameter-less constructor for " + email.FullName + " found");

            return (Email)constructor.Invoke(new object[0]);
        }

        /// <summary>
        /// The name of the cookie used to identify a logged in user.
        /// </summary>
        public static readonly string UserCookieName = "usr";

        /// <summary>
        /// The name of the cookie used to identify an anonymous user.
        /// 
        /// Useful for when we want to make sure a submitted token actually
        /// goes with a "user".
        /// </summary>
        public static readonly string AnonymousCookieName = "anon";

        /// <summary>
        /// This is salt is used for anything we want *hashed* but need to be able to lookup.
        /// 
        /// The salt only serves to make correlating data *between* databases more difficult, 
        /// it doesn't make rainbow table style pre-computation (provided the salt has been leaked) impossible.
        /// 
        /// As such the system wide salt should be treated as pseudo-secret.  Don't publish it, and routintely cycle it.
        /// </summary>
        private static string SiteWideSalt { get { return KeyStore.GetKey(KeyStore.LatestKeyVersion).Salt; } }

        /// <summary>
        /// In memory copy of the AES key for this instance.
        /// </summary>
        private static byte[] AesKey { get { return Convert.FromBase64String(KeyStore.GetKey(AesKeyVersion).Encryption); } }

        /// <summary>
        /// The "version" of the key.
        /// 
        /// In the event of a key leak, storing this alongside encrypted values
        /// will let us do piece-meal re-encryption.
        /// </summary>
        private static byte AesKeyVersion { get { return KeyStore.LatestKeyVersion; } }

        /// <summary>
        /// Provides encryption & decryption services for the entire instance.
        /// </summary>
        private static AesCryptoServiceProvider AesProvider;

        /// <summary>
        /// Instance wide source for all random numbers.
        /// 
        /// Only access through Random(), for locking purposes.
        /// </summary>
        private static RandomNumberGenerator RandomSource = RandomNumberGenerator.Create();

        private static HMAC HMACProvider;
        /// <summary>
        /// Provides message authentication codes for the entire instance.
        /// </summary>
        private static HMAC HMAC
        {
            get
            {
                if (HMACProvider != null) return HMACProvider;

                HMACProvider = new HMACSHA1();
                HMAC.Key = Convert.FromBase64String(KeyStore.GetKey(AesKeyVersion).HMAC);

                return HMACProvider;
            }
        }

        private static string CacheBreakerCached;
        /// <summary>
        /// Site wide cache breaker.
        /// 
        /// Guaranteed to cycle across builds (but not across AppPool cycles).
        /// 
        /// Stick it onto resources we need clients to re-load after each build, 
        /// so as to pickup changes.
        /// </summary>
        public static string CacheBreaker
        {
            get
            {
                if (CacheBreakerCached == null)
                {
                    // Whenever we build, the dll write time should change
                    var asm = Assembly.GetExecutingAssembly();
                    var dllLoc = asm.Location;

                    var dllLastModified = File.GetLastWriteTimeUtc(dllLoc);

                    var ms = dllLastModified.Ticks / 10000;
                    var secs = ms / 1000;
                    var mins = ms / 60;
                    var fiveMins = mins / 5;

                    CacheBreakerCached = Current.WeakHash(fiveMins.ToString());
                }

                return CacheBreakerCached;
            }
        }

        private const int DefaultHashIterations = 1000;
        private static int? HashIterationsCached;
        /// <summary>
        /// The number of iterations to use when calculating a Secure or System hash.
        /// 
        /// This value can vary over the lifetime of the site, so it should be stored.
        /// </summary>
        private static int HashIterations
        {
            get
            {
                if (!HashIterationsCached.HasValue)
                {
                    if (WebConfigurationManager.AppSettings.AllKeys.Contains("HashIterations"))
                    {
                        int val;
                        if (int.TryParse(WebConfigurationManager.AppSettings["HashIterations"], out val))
                        {
                            if (val < DefaultHashIterations) throw new ArgumentException("HashIterations cannot be less than 4000");
                            HashIterationsCached = val;
                        }
                        else
                        {
                            HashIterationsCached = DefaultHashIterations;
                        }
                    }
                    else
                    {
                        HashIterationsCached = DefaultHashIterations;
                    }
                }

                return HashIterationsCached.Value;
            }
        }

        // Salt sizes throughout the system
        private const int SALT_SIZE = 16;

        static Current()
        {
            AesProvider = new AesCryptoServiceProvider();
        }

        /// <summary>
        /// Right this instant.
        /// 
        /// This date it UTC (and all dates in the system should also be UTC).
        /// </summary>
        public static DateTime Now
        {
            get { return DateTime.UtcNow; }
        }

        /// <summary>
        /// Returns true if the current request should be rejected due to failing
        /// some integrity check.
        /// 
        /// This is handled in Current, as rejections at "lower levels" are often
        /// hard to direct to meaningful error pages.
        /// </summary>
        public static bool RejectRequest
        {
            get
            {
                var cached = GetFromContext<string>("RejectRequest");

                if (cached == null || bool.Parse(cached)) return true;

                return false;
            }
            set
            {
                SetInContext("RejectRequest", value.ToString());
            }
        }

        /// <summary>
        /// Set on requests that were sent to routes expecting POST, but
        /// POST was not sent.
        /// 
        /// Used for displaying better error messages, because this event
        /// happens quite a bit lower in the stack than any Controller events.
        /// </summary>
        public static bool PostExpectedAndNotReceived
        {
            get
            {
                var cached = GetFromContext<string>("PostExpectedAndNotReceived");
                if (cached == null) return false;

                return bool.Parse(cached);
            }
            set
            {
                SetInContext("PostExpectedAndNotReceived", value.ToString());
            }
        }

        /// <summary>
        /// When set, overrides default cache directives to explicitly indicate
        /// that a response should not be cached at all.
        /// 
        /// This is done via Cache-Control and Pragma headers, ultimately.
        /// </summary>
        public static bool NoCache
        {
            get
            {
                var cached = GetFromContext<string>("NoCache");
                if (cached == null) return false;

                return bool.Parse(cached);
            }
            set
            {
                SetInContext("NoCache", value.ToString());
            }
        }

        /// <summary>
        /// Returns whether the current request should result in a 
        /// page that tries to bust out of frames.
        /// 
        /// Defaults to true.
        /// </summary>
        public static bool ShouldBustFrames
        {
            get
            {
                var cached = GetFromContext<string>("ShouldBustFrames");

                if (cached == null || bool.Parse(cached)) return true;

                return false;
            }

            set
            {
                SetInContext("ShouldBustFrames", value.ToString());
            }
        }

        /// <summary>
        /// A read-only connection to the DB.
        /// 
        /// Should be used in all cases where update/inserts/deletes are not needed.
        /// </summary>
        public static DBContext ReadDB
        {
            get
            {
                var cached = GetFromContext<DBContext>("ReadDB");

                if (cached != null) return cached;

                cached = GetDB(ReadConnectionString);

                SetInContext("ReadDB", cached);
                return cached;
            }
        }

        /// <summary>
        /// A read-write connection to the DB.
        /// 
        /// Should be touched by routes which themselves are the result of a 
        /// POST being received, or in response to internal "needs update" checks.
        /// </summary>
        public static DBContext WriteDB
        {
            get
            {
                var isPost = HttpContext.Current != null ? HttpContext.Current.Request.HttpMethod == "POST" : false;

                var cached = GetFromContext<DBContext>("WriteDB");

                if (cached == null)
                {
                    cached = GetDB(WriteConnectionString);
                    SetInContext("WriteDB", cached);
                }

                // If this isn't in response to a POST, severly restrict what this connection can do
                cached.RestrictToCurrentUserAttributes = !isPost;

                return cached;
            }
        }

        private static DBContext GetDB(string connectionString)
        {
            var conn = new SqlConnection(connectionString);
            var wrapped = ProfiledDbConnection.Get(conn);

            return new DBContext(wrapped);
        }

        /// <summary>
        /// The path this request is being served from.
        /// 
        /// http://example.com/ for instance
        /// </summary>
        public static Uri AppRootUri
        {
            get
            {
                string protocol;
#if DEBUG_HTTP
                protocol = "http://";
#else
                protocol = "https://";
#endif

                var ret = RequestUri.Host;
                ret = protocol + ret;

                if (!ret.EndsWith("/")) ret += "/";

                return new Uri(ret);
            }
        }

        /// <summary>
        /// The URL the current request was made to.
        /// </summary>
        public static Uri RequestUri
        {
            get
            {
                var url = HttpContext.Current.Request.Url.ToString();

                return new Uri(CorrectHttpAndPort(url));
            }
        }

        /// <summary>
        /// The currently logged in user (or null, if the user is annonymous).
        /// </summary>
        public static User LoggedInUser
        {
            get
            {
                var cached = GetFromContext<User>("LoggedInUser");
                if (cached != null) return cached;

                using (MiniProfiler.Current.Step("LoggedInUser"))
                {
                    var cookie = HttpContext.Current.CookieSentOrReceived(UserCookieName);

                    if (cookie == null || cookie.Value == null) return null;

                    var hash = Current.WeakHash(cookie.Value);
                    cached = Current.ReadDB.Users.SingleOrDefault(u => u.SessionHash == hash);

                    SetInContext("LoggedInUser", cached);

                    if (cached != null && cached.IsAdministrator)
                    {
                        if (!HttpContext.Current.Request.Cookies.AllKeys.Contains(MvcApplication.ProfilerCookieName))
                        {
                            HttpContext.Current.Response.Cookies.Add(new HttpCookie(MvcApplication.ProfilerCookieName, "and-its-going-fast"));
                        }
                    }

                    return cached;
                }
            }

            set
            {
                if (value != null) throw new ArgumentException("Can only clear the LoggedInUser (forcing a new lookup), not set it directly.");

                SetInContext<User>("LoggedInUser", null);
            }
        }

        

        /// <summary>
        /// The IP address (v4 or v6) that the current request originated from.
        /// 
        /// This is not a valid source of user id or uniqueness, but should be logged
        /// with most modifying operations.
        /// </summary>
        public static string RemoteIP
        {
            get
            {
                var serverVars = HttpContext.Current.Request.ServerVariables;

                var headers = HttpContext.Current.Request.Headers;

                return GetRemoteIP(headers["X-Real-IP"], serverVars["REMOTE_ADDR"], headers["X-Forwarded-For"]);
            }
        }

        // Pulls out whatever "looks" like an IPv4 or v6 address that ends a string
        private static Regex LastAddress = new Regex(@"\b(\d|a-f|\.|:)+$", RegexOptions.Compiled);

        /// <summary>
        /// Takes in the REMOTE_ADDR and X-Forwarded-For headers and returns what
        /// we consider the current requests IP to be, for logging and throttling 
        /// purposes.
        /// 
        /// The logic is, basically, if xForwardedFor *has* a value and the apparent
        /// IP (the last one in the hop) is not local; use that.  Otherwise, use remoteAddr.
        /// </summary>
        public static string GetRemoteIP(string realIp, string remoteAddr, string xForwardedFor)
        {
            // if we've got an authoritative ip, use it
            if (realIp.HasValue() && !IsPrivateIP(realIp))
            {
                return realIp;
            }

            // check if we were forwarded from a proxy
            if (xForwardedFor.HasValue())
            {
                // workarround, check if in the X-Forwarded-For has a port number like in Azure webapp hosting:
                xForwardedFor = LastAddress.Match(xForwardedFor).Value;
                string[] parts = xForwardedFor.Split(':');
                if (parts.Length == 1)
                {
                    //original:
                    if (xForwardedFor.HasValue() && !IsPrivateIP(xForwardedFor))
                        remoteAddr = xForwardedFor;
                    //end
                }
                else if (parts.Length == 2)
                {
                    string host = parts[0];
                    string port = parts[1];
                    remoteAddr = host;
                }
                else
                {
                    // throw error
                    throw new Exception(string.Format("X-Forwarded-For has an invalid value, with more than host + port {0}", xForwardedFor.ToString()));
                }                                
            }

            // Something weird is going on, bail
            if (!remoteAddr.HasValue()) throw new Exception("Cannot determine source of request");

            return remoteAddr;
        }

        /// <summary>
        /// Returns true if the current request is from an internal IP address.
        /// </summary>
        public static bool IsInternalRequest
        {
            get
            {
                return IsPrivateIP(RemoteIP);
            }
        }

        /// <summary>
        /// Returns the XSRF token expected on the current request (if any).
        /// 
        /// This value is dependent on the presense of either the user cookie, or 
        /// a temporary anonymous cookie (as in login).
        /// </summary>
        public static Guid? XSRFToken
        {
            get
            {
                var user = HttpContext.Current.CookieSentOrReceived(UserCookieName);

                string toHash;

                if (user != null && user.Value.HasValue())
                {
                    toHash = user.Value;
                }
                else
                {
                    var anon = HttpContext.Current.CookieSentOrReceived(AnonymousCookieName);

                    if (anon == null) return null;

                    toHash = anon.Value;
                }

                if (toHash == null) return null;

                var hash = Current.WeakHash(toHash);

                var retStr = Current.GetFromCache<string>("xsrf-" + hash);

                // If the user is logged in and *needs* an XSRF, we should just create one if there isn't already one
                if (retStr == null && user != null)
                    return GenerateXSRFToken();

                // Things got a little wonky, need to clean up
                if (retStr == null)
                {
                    KillCookie(AnonymousCookieName);
                    throw new Exception("Bad cookie keyed XSRF token");
                }
                
                return Guid.Parse(retStr);
            }
        }

        /// <summary>
        /// Returns the current controller.
        /// 
        /// Useful for places where you need a Controller or ControllerContext.
        /// 
        /// Honestly, this is a tad hacky; since the only place we need those
        /// Controllers or ControllerContexts is when we're rendering a view
        /// to a string.
        /// 
        /// Would be nice if asp.net mvc supported that "out of the box".
        /// </summary>
        public static ControllerBase Controller
        {
            get
            {
                return GetFromContext<Controller>("Controller");
            }
            set
            {
                SetInContext("Controller", value);
            }
        }

        /// <summary>
        /// Destroys any existing tokens for the current user, and creates a new one.
        /// </summary>
        private static Guid GenerateXSRFToken()
        {
            if (LoggedInUser == null) throw new Exception("Cannot generated an XSRF token this way for an anonymous user.");

            var ret = UniqueId();

            var newToken =
                new
                {
                    CookieHash = LoggedInUser.SessionHash,
                    CreationDate = Current.Now,
                    Token = ret
                };

            Current.AddToCache("xsrf-" + newToken.CookieHash, ret.ToString(), TimeSpan.FromDays(1));

            return ret;
        }

        /// <summary>
        /// Create a new XSRF token, and attach a cookie to the user so we can look it up after a POST.
        /// </summary>
        public static void GenerateAnonymousXSRFCookie()
        {
            var random = UniqueId().ToString();

            Current.AddCookie(Current.AnonymousCookieName, random, TimeSpan.FromMinutes(15));

            var token = UniqueId();
            var cookieHash = Current.WeakHash(random);

            Current.AddToCache("xsrf-" + cookieHash, token.ToString(), TimeSpan.FromMinutes(15));

            // Can't allow for anon and usr to coexist, things get funky.
            if (HttpContext.Current.Request.Cookies.AllKeys.Contains(Current.UserCookieName))
            {
                Current.KillCookie(Current.UserCookieName);
            }
        }

        private static Regex _httpMatch = new Regex(@"^http://", RegexOptions.Compiled);
        private static Regex _portMatch = new Regex(@":\d+", RegexOptions.Compiled);
        /// <summary>
        /// Anything we expose needs to be sanitized of our port/ssl tricks.
        /// 
        /// Thus, all urls must start https:// and must *not* have :post-number on them.
        /// </summary>
        public static string CorrectHttpAndPort(string url)
        {
#if !DEBUG_HTTP
            url = _httpMatch.Replace(url, "https://");
            url = _portMatch.Replace(url, "");
#endif

            return url;
        }

        /// <summary>
        /// Takes in url fragment, and rebases it as under the domain of the current request.
        /// 
        /// ie. Url("hello-world?testing") on the http://example.com/ domain becomes
        /// http://example.com/hello-world?testing
        /// </summary>
        public static string Url(string fragment)
        {
            return new Uri(AppRootUri, fragment).AbsoluteUri;
        }

        /// <summary>
        /// Adds a "kill cookie" to the response to the current request.
        /// 
        /// Causes (well, asks nicely) the client to discard any cookie they have with the given name.
        /// 
        /// Does nothing if the cookie can't be found in the request.
        /// </summary>
        public static void KillCookie(string cookieName)
        {
            if (!HttpContext.Current.Request.Cookies.AllKeys.Contains(cookieName)) return;

            var killCookie = new HttpCookie(cookieName);
            killCookie.Expires = Current.Now.AddMinutes(-15);
            killCookie.HttpOnly = true;
            killCookie.Secure = true;

            HttpContext.Current.Response.Cookies.Add(killCookie);
        }

        /// <summary>
        /// Adds (or updates) a cookie to the response to the current request.
        /// 
        /// Should always be used in favor 
        /// </summary>
        public static void AddCookie(string cookieName, string value, TimeSpan expiresIn)
        {
            var cookie = new HttpCookie(cookieName);
            cookie.Value = value;
            cookie.Expires = Current.Now + expiresIn;
            cookie.HttpOnly = true;

#if !DEBUG_HTTP
            cookie.Secure = true;
#endif

            HttpContext.Current.Response.Cookies.Add(cookie);

            // http://stackoverflow.com/questions/389456/cookie-blocked-not-saved-in-iframe-in-internet-explorer
            //   Basically, this tells IE that we're not doing anything nefarious (just tracking for tailoring and dev purposes)
            //   ... no other browser even pretends to care.
            HttpContext.Current.Response.Headers["p3p"] = @"CP=""NOI CURa ADMa DEVa TAIa OUR BUS IND UNI COM NAV INT""";
        }

        /// <summary>
        /// Insert something into the (machine local) cache.
        /// </summary>
        public static void AddToCache<T>(string name, T o, TimeSpan expiresIn) where T : class
        {
            // No point trying to cache null values
            if (o != null)
            {
                var redis = Redis;

                if (redis == null)
                {
                    HttpRuntime.Cache.Insert(name, o, null, Current.Now + expiresIn, Cache.NoSlidingExpiration);
                }
                else
                {
                    byte[] bytes;
                    using (var stream = new MemoryStream())
                    {
                        Serializer.Serialize<T>(stream, o);
                        bytes = stream.ToArray();
                    }

                    var task = redis.SetWithExpiry(RedisDB.Value.Value, "oid-" + name, (int)expiresIn.TotalSeconds, bytes, true);
                    redis.Wait(task);
                }
            }
        }

        /// <summary>
        /// Get something from the (machine local) cache.
        /// </summary>
        public static T GetFromCache<T>(string name) where T : class
        {
            var redis = Redis;

            if (redis == null)
            {
                return HttpRuntime.Cache[name] as T;
            }
            else
            {
                var reps = redis.Get(RedisDB.Value.Value, "oid-" + name, false);
                var bytes = reps.Result;

                if (bytes == null) return null;

                using (var stream = new MemoryStream(bytes))
                {
                    return Serializer.Deserialize<T>(stream);
                }
            }
        }

        /// <summary>
        /// Invalidate a key in the cache.
        /// 
        /// Returns true if the key was present in the cache to be removed, and false otherwise.
        /// </summary>
        public static bool RemoveFromCache(string name)
        {
            var redis = Redis;

            if (redis == null)
            {
                var oldValue = HttpRuntime.Cache.Remove(name);

                return oldValue != null;
            }
            else
            {
                var task = redis.Remove(RedisDB.Value.Value, "oid-" + name, true);

                return task.Result;
            }
        }

        /// <summary>
        /// Get something from the per-request cache.
        /// 
        /// Returns null if not found.
        /// </summary>
        private static T GetFromContext<T>(string name) where T : class
        {
            return HttpContext.Current.Items[name] as T;
        }

        /// <summary>
        /// Place something in the per-request cache.
        /// 
        /// Once this HttpRequest is complete, it will be lost.
        /// 
        /// Reference types only.
        /// </summary>
        private static void SetInContext<T>(string name, T value) where T : class
        {
            HttpContext.Current.Items[name] = value;
        }

        /// <summary>
        /// Returns true if this is a private network IP (v4 or v6)
        /// http://en.wikipedia.org/wiki/Private_network
        /// </summary>
        internal static bool IsPrivateIP(string s)
        {
            var ipv4Check = (s.StartsWith("192.168.") || s.StartsWith("10.") || s.StartsWith("127.0.0."));

            if (ipv4Check) return true;

            IPAddress addr;
            
            if(!IPAddress.TryParse(s, out addr) || addr.AddressFamily != AddressFamily.InterNetworkV6) return false;

            // IPv6 reserves fc00::/7 for local usage
            // http://en.wikipedia.org/wiki/Unique_local_address
            var address = addr.GetAddressBytes();
            return address[0] == (byte)0xFD;    //FC + the L-bit set to make FD
        }

        /// <summary>
        /// Generate a *truly* random (that is, version 4) GUID.
        /// 
        /// Guids that are never exposed externally can be safely obtained from Guid.NewGuid(), 
        /// but when in doubt use this function.
        /// 
        /// Nice overview of normal GUID generation: http://blogs.msdn.com/b/oldnewthing/archive/2008/06/27/8659071.aspx
        /// 
        /// Version 4 is described here: http://en.wikipedia.org/wiki/Universally_unique_identifier
        /// </summary>
        public static Guid UniqueId()
        {
            var bytes = Random(16);
            bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40);  // Set the GUID version to 4
            bytes[8] = (byte)((bytes[8] & 0x0F) | (0x80 + (Random(1)[0] % 4))); // tweaking 8th byte as required
            
            return new Guid(bytes);
        }

        /// <summary>
        /// Return a base64 encoded version of an HMAC for
        /// the give byte array.
        /// 
        /// Note that this uses the *current* key version,
        /// generally this is correct but may require some...
        /// finese in others.
        /// </summary>
        private static string MakeAuthCode(byte[] toSign, HMAC hmac = null)
        {
            using (MiniProfiler.Current.Step("MakeAuthCode"))
            {
                if (hmac == null) hmac = HMAC;

                lock (hmac)
                    return Convert.ToBase64String(hmac.ComputeHash(toSign));
            }
        }

        /// <summary>
        /// Imposes a canonical ordering on a key=>value pair set, 
        /// and then HMACs it.  Returns a base64 encoded version of the
        /// result.
        /// 
        /// The actual key=>value pairs are the properties on an object,
        /// expected usage is
        /// MakeAuthCode(new { blah, moreBlah, andSoOn });
        /// </summary>
        public static string MakeAuthCode(object toSign, HMAC hmac = null)
        {
            var props = toSign.PropertiesAsStrings();

            string signString = "";

            foreach (var prop in props.OrderBy(p => p.Key))
            {
                signString += prop.Key + "=" + prop.Value + "&";
            }

            // trim off trailing '&'
            if (signString.Length > 0)
            {
                signString = signString.Substring(0, signString.Length - 1);
            }

            return MakeAuthCode(Encoding.UTF8.GetBytes(signString), hmac);
        }

        /// <summary>
        /// Encrypts a value using the system wide key.
        /// 
        /// Returns the key *version*, which should be stored with
        /// any encrypted value in the event that the key is leaked 
        /// and all values need to be re-encrypted.
        /// 
        /// The result is base64 encoded.
        /// </summary>
        public static string Encrypt(string value, out string iv, out byte version, out string hmac)
        {
            using (MiniProfiler.Current.Step("Encrypt"))
            {
                version = AesKeyVersion;
                var ivBytes = Random(16);
                iv = Convert.ToBase64String(ivBytes);

                ICryptoTransform encryptor;

                lock (AesProvider)
                    encryptor = AesProvider.CreateEncryptor(AesKey, ivBytes);

                byte[] output;

                using (encryptor)
                {
                    var input = Encoding.UTF8.GetBytes(value);
                    output = encryptor.TransformFinalBlock(input, 0, input.Length);
                }

                hmac = MakeAuthCode(output);

                return Convert.ToBase64String(output);
            }
        }

        /// <summary>
        /// Decrypts a value using the system wide key.
        /// 
        /// Expects encrypted to be encoded in base64.
        /// 
        /// If the byte version doesn't match the current key version,
        /// outOfDate will be set and the value should be re-encrypted and stored
        /// before completing any other operation.
        /// </summary>
        public static string Decrypt(string encrypted, string iv, byte version, string hmac, out bool outOfDate)
        {
            using (MiniProfiler.Current.Step("Decrypt"))
            {
                outOfDate = false;

                var encryptedBytes = Convert.FromBase64String(encrypted);
                var ivBytes = Convert.FromBase64String(iv);

                // Value encrypted using an old key encountered
                if (version != AesKeyVersion)
                {
                    var oldKey = KeyStore.GetKey(version);

                    // Different crypto keys means different hmac keys, gotta spin up an old one
                    var oldHmac = new HMACSHA1();
                    oldHmac.Key = Convert.FromBase64String(oldKey.HMAC);

                    if (hmac != Convert.ToBase64String(oldHmac.ComputeHash(Convert.FromBase64String(encrypted))))
                        throw new Exception("HMAC validation failed on encrypted value (key version = " + oldKey.Version + ")");

                    ICryptoTransform oldDecryptor;

                    lock (AesProvider)
                        oldDecryptor = AesProvider.CreateDecryptor(Convert.FromBase64String(oldKey.Encryption), ivBytes);


                    var retBytes = oldDecryptor.TransformFinalBlock(encryptedBytes, 0, encryptedBytes.Length);

                    outOfDate = true;
                    return Encoding.UTF8.GetString(retBytes);
                }

                var shouldMatchHMAC = MakeAuthCode(Convert.FromBase64String(encrypted));

                if (hmac != shouldMatchHMAC)
                    throw new Exception("HMAC validation failed on encrypted value");

                ICryptoTransform decryptor;
                lock (AesProvider)
                    decryptor = AesProvider.CreateDecryptor(AesKey, ivBytes);

                var ret = decryptor.TransformFinalBlock(encryptedBytes, 0, encryptedBytes.Length);

                return Encoding.UTF8.GetString(ret);
            }
        }

        /// <summary>
        /// Cranks out a collision resistant hash, relatively quickly.
        /// 
        /// Not suitable for passwords, or sensitive information.
        /// </summary>
        public static string WeakHash(string value)
        {
            using (MiniProfiler.Current.Step("WeakHash"))
            {
                var hasher = SHA1.Create();

                byte[] bytes = value.HasValue() ? Encoding.UTF8.GetBytes(value) : new byte[0];

                return Convert.ToBase64String(hasher.ComputeHash(bytes));
            }
        }

        /// <summary>
        /// Cranks out a hash suitable for a lookup.
        /// 
        /// In cases where arbitrary lookup is not required, but 
        /// potentially valuable information is involved,
        /// use SecureHash instead.
        /// 
        /// In cases where the hash is ephemeral (quickly expiring, or
        /// not of valuable information), use WeakHash.
        /// </summary>
        public static string SystemHash(string value, out byte saltVersion)
        {
            using (MiniProfiler.Current.Step("SystemHash"))
            {
                var salt = SiteWideSalt;
                saltVersion = KeyStore.LatestKeyVersion;

                return SecureHash(value, salt);
            }
        }

        /// <summary>
        /// Cranks out a secure hash, generating a new salt.
        /// </summary>
        public static string SecureHash(string value, out string salt)
        {
            using (MiniProfiler.Current.Step("SecureHashMakeSalt"))
            {
                salt = GenerateSalt();

                return Hash(value, salt);
            }
        }

        /// <summary>
        /// Cranks out a secure hash with a specific salt
        /// </summary>
        public static string SecureHash(string value, string salt)
        {
            using (MiniProfiler.Current.Step("SecureHashWithSalt"))
            {
                return Hash(value, salt);
            }
        }

        /// <summary>
        /// Universal random provider.
        /// 
        /// Just for paranoia's sake, use it for all random purposes.
        /// </summary>
        public static byte[] Random(int bytes)
        {
            using (MiniProfiler.Current.Step("Random"))
            {
                var ret = new byte[bytes];

                lock (RandomSource)
                    RandomSource.GetBytes(ret);

                return ret;
            }
        }

        /// <summary>
        /// Convenience wrapper around Random to grab a new salt value.
        /// Treat this value as opaque, as it captures iterations.
        /// </summary>
        public static string GenerateSalt(int? explicitIterations = null)
        {
            if (explicitIterations.HasValue && explicitIterations.Value < DefaultHashIterations)
                throw new ArgumentException("Cannot be less than " + DefaultHashIterations, "explicitIterations");

            var bytes = Random(SALT_SIZE);

            var iterations = (explicitIterations ?? HashIterations).ToString("X");

            return iterations + "." + Convert.ToBase64String(bytes);
        }

        /// <summary>
        /// Backs Secure and System hashes.
        /// 
        /// Uses PBKDF2 internally, as implemented by the Rfc2998DeriveBytes class.
        /// 
        /// See http://en.wikipedia.org/wiki/PBKDF2
        /// and http://msdn.microsoft.com/en-us/library/bwx8t0yt.aspx
        /// </summary>
        private static string Hash(string value, string salt)
        {
            using (MiniProfiler.Current.Step("Hash"))
            {
                var i = salt.IndexOf('.');
                var iters = int.Parse(salt.Substring(0, i), System.Globalization.NumberStyles.HexNumber);
                salt = salt.Substring(i + 1);

                using (var pbkdf2 = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(value), Convert.FromBase64String(salt), iters))
                {
                    var key = pbkdf2.GetBytes(24);

                    return Convert.ToBase64String(key);
                }
            }
        }

        /// <summary>
        /// Log an exception to disk.
        /// </summary>
        public static void LogException(Exception e)
        {
            (new Error(e)).Log(ErrorLogPath);
        }
    }
}