﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Web.Mvc;
using OpenIdProvider.Helpers;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using OpenIdProvider.Models;

namespace OpenIdProvider.Controllers
{
    /// <summary>
    /// Contains all adminstrative functions.
    /// </summary>
    public class AdminController : ControllerBase
    {
        /// <summary>
        /// Route for testing the error handler is functioning.
        /// </summary>
        [Route("admin/throw", AuthorizedUser.Administrator)]
        public ActionResult Throw()
        {
            throw new Exception("Test exception via admin/throw");
        }

        /// <summary>
        /// Simple index of all /admin routes
        /// </summary>
        [Route("admin", AuthorizedUser.Administrator)]
        public ActionResult Index()
        {
            return View();
        }

        [Route("admin/find-user", AuthorizedUser.Administrator)]
        public ActionResult FindUser(string email)
        {
            var user = Models.User.FindUserByEmail(email);

            return
                user == null ?
                TextPlain("Not Found") :
                new ContentResult { ContentType = "text/html", Content = "<html><body><a href='" + user.GetClaimedIdentifier() + "'>user</a></body></html>" };
        }

        [Route("admin/find-users/batch", AuthorizedUser.Administrator)]
        public ActionResult FindUsersBatch()
        {
            return View();
        }

        [Route("admin/find-users/batch/submit", HttpVerbs.Post, AuthorizedUser.Administrator)]
        public ActionResult FindUsersBatchSubmit(string emails)
        {
            var ems = emails.Split(' ').Select(s => s.Trim()).ToList();

            var result = new List<User>();

            foreach (var em in ems)
            {
                var user = Models.User.FindUserByEmail(em);
                if (user != null)
                {
                    result.Add(user);
                }
            }

            return Json(result.Select(s => new { Email = s.Email, Link = s.GetClaimedIdentifier().ToString() }).ToArray());
        }

        /// <summary>
        /// List users registred in OpenID provider
        /// </summary>
        /// <param name="showall">isToShowAll</param>
        /// <param name="page">Page Number</param>
        /// <param name="pagesize">page Size</param>
        /// <returns></returns>
        [Route("admin/list-users/view", HttpVerbs.Post, AuthorizedUser.Administrator)]
        public ActionResult ListUsers(bool? showall, int? page, int? pagesize)
        {
            var all = showall.GetValueOrDefault(false);
            var p = page.GetValueOrDefault(0);
            var ps = pagesize.GetValueOrDefault(30);

            var bans = Current.ReadDB.IPBans.AsQueryable();
            var listUsers = Current.ReadDB.Users.AsQueryable();
            //if (!all) listUsers = listUsers.Where(u => u.IsAdministrator == false);

            ViewData["count"] = listUsers.Count();

            listUsers = listUsers.OrderByDescending(u => u.CreationDate).Skip(ps * p).Take(ps);
            ViewData["page"] = p;
            ViewData["pagesize"] = ps;
            ViewData["showall"] = all;

            return View(listUsers.ToList());
        }

        /// <summary>
        /// List all errors in a handy web interface
        /// </summary>
        [Route("admin/errors", AuthorizedUser.Administrator)]
        public ActionResult ListErrors(int? pagesize, int? page)
        {
            int total;
            var errors = Error.LoadErrors(Current.ErrorLogPath, pagesize.GetValueOrDefault(30), page.GetValueOrDefault(1) - 1, out total);

            ViewData["total"] = total;
            ViewData["pagesize"] = pagesize.GetValueOrDefault(30);
            ViewData["page"] = page.GetValueOrDefault(1);

            return View(errors);
        }

        /// <summary>
        /// View a single error.
        /// </summary>
        [Route("admin/error/{id}", RoutePriority.Low, AuthorizedUser.Administrator)]
        public ActionResult ViewError(string id)
        {
            Guid errorId;
            if (id.IsNullOrEmpty() || !Guid.TryParse(id, out errorId)) return NotFound();

            var error = Error.LoadError(Current.ErrorLogPath, errorId);

            if (error == null) return NotFound();

            return View(error);
        }

        /// <summary>
        /// Delete an error, given its id.
        /// </summary>
        [Route("admin/error/delete/submit", HttpVerbs.Post, AuthorizedUser.Administrator)]
        public ActionResult DeleteError(string id, int? pagesize, int? page)
        {
            //if (!Current.IsInternalRequest) return NotFound();

            Guid errorId;
            if (id.IsNullOrEmpty() || !Guid.TryParse(id, out errorId)) return NotFound();

            var error = Error.LoadError(Current.ErrorLogPath, errorId);

            if (error == null) return NotFound();

            error.Delete();

            return
                SafeRedirect(
                    (Func<int?, int?, ActionResult>)ListErrors,
                    new {
                        pagesize,
                        page
                    });
        }

#if DEBUG

        /// <summary>
        /// Generate a new key, that can be added to the keystore file.
        /// 
        /// Not really protected by much, as generating the key does nothing,
        /// it still has to be added to the actual file.
        /// </summary>
        [Route("admin/key-gen", AuthorizedUser.Administrator | AuthorizedUser.LoggedIn | AuthorizedUser.Anonymous)]
        public ActionResult GenerateKey()
        {
            var crypto = new AesCryptoServiceProvider();
            crypto.GenerateKey();

            var key = Convert.ToBase64String(crypto.Key);
            var salt = Current.GenerateSalt();
            var hmac = Convert.ToBase64String(Current.Random(64));

            var ret =
                Newtonsoft.Json.JsonConvert.SerializeObject(
                    new KeyStore.Key
                        {
                            Version = 255,
                            Encryption = key,
                            Salt = salt,
                            HMAC = hmac
                        });

            return TextPlain(ret);
        }

#endif

        /// <summary>
        /// List all ip bans for the site, and provides some minor
        /// UI for adding/removing them.
        /// </summary>
        [Route("admin/ip-bans", AuthorizedUser.Administrator)]
        public ActionResult IPBans(bool? showall, int? page, int? pagesize)
        {
            var all = showall.GetValueOrDefault(false);
            var p = page.GetValueOrDefault(0);
            var ps = pagesize.GetValueOrDefault(30);

            var bans = Current.ReadDB.IPBans.AsQueryable();

            if (!all) bans = bans.Where(b => b.ExpirationDate > Current.Now);

            ViewData["count"] = bans.Count();

            bans = bans.OrderByDescending(b => b.CreationDate).Skip(ps * p).Take(ps);

            ViewData["page"] = p;
            ViewData["pagesize"] = ps;
            ViewData["showall"] = all;

            return View(bans.ToList());
        }

        /// <summary>
        /// Landing route for when an error is encountered with admin/ip-bans/create/submit.
        /// 
        /// Lets us get away with using RecoverableError in some convenient places.
        /// </summary>
        [Route("admin/ip-bans/create", AuthorizedUser.Administrator)]
        public ActionResult IPBansCreateLanding(bool? showAll, int? page, int? pagesize)
        {
            return IPBans(showAll, page, pagesize);
        }

        /// <summary>
        /// Deletes (sets expiration to *now*) an IP ban.
        /// </summary>
        [Route("admin/ip-bans/delete/submit", HttpVerbs.Post, AuthorizedUser.Administrator)]
        public ActionResult DeleteIPBan(int? id, int? pagesize, int? page, bool? showall)
        {
            if (id.HasValue)
            {
                var db = Current.WriteDB;

                var ban = db.IPBans.SingleOrDefault(i => i.Id == id);

                if (ban != null)
                {
                    ban.ExpirationDate = Current.Now;
                    db.SubmitChanges();
                }
            }
            else
            {
                return NotFound();
            }

            return SafeRedirect(
                (Func<bool?, int?, int?, ActionResult>)IPBans,
                new 
                {
                    showall,
                    page,
                    pagesize
                });
        }

        /// <summary>
        /// Creates a new IP ban.
        /// </summary>
        [Route("admin/ip-bans/create/submit", HttpVerbs.Post, AuthorizedUser.Administrator)]
        public ActionResult CreateIPBan(string ip, string expires, string reason, bool? showall, int? page, int? pagesize)
        {
            var retryValues = new { ip, expires, reason };

            if (!ip.HasValue()) return RecoverableError("IP must be set.", retryValues);
            if (!expires.HasValue()) return RecoverableError("Expires must be set.", retryValues);
            if (!reason.HasValue()) return RecoverableError("Reason must be set.", retryValues);

            DateTime expDate;
            if (!DateTime.TryParse(expires, out expDate)) return RecoverableError("Expires not recognized as a date.", retryValues);

            var now = Current.Now;

            if (expDate < now) return RecoverableError("Expiration date must be in the future.", retryValues);

            var newBan =
                new IPBan
                {
                    CreationDate = now,
                    ExpirationDate = expDate,
                    IP = ip,
                    Reason = reason
                };

            var db = Current.WriteDB;
            db.IPBans.InsertOnSubmit(newBan);
            db.SubmitChanges();

            return SafeRedirect(
                (Func<bool?, int?, int?, ActionResult>)IPBans,
                new
                {
                    showall,
                    page,
                    pagesize
                });
        }

        [Route("admin/import-aspnet", AuthorizedUser.Administrator)]
        public ActionResult ImportAspNetUsers()
        {
            return View();
        }

        [Route("admin/import-aspnet/submit", HttpVerbs.Post, AuthorizedUser.Administrator)]
        public ActionResult ImportAspNetUserSubmit(string email, string salt, string password)
        {
            if (email.IsNullOrEmpty() || salt.IsNullOrEmpty() || password.IsNullOrEmpty())
            {
                return Content("Missing required value", "text/html");
            }

            // Prevents us from creating any user we wouldn't do otherwise
            string token, authCode, error;
            if (!PendingUser.CreatePendingUser(email, Guid.NewGuid().ToString(), null, out token, out authCode, out error))
            {

                return Content("<font color='red'>For [" + email + "] - " + error + "</font>", "text/html");
            }

            // Change the u/p to what's expected
            var pending = Current.WriteDB.PendingUsers.Single(u => u.AuthCode == authCode);
            pending.PasswordSalt = salt;
            pending.PasswordHash = password;
            Current.WriteDB.SubmitChanges();

            User newUser;
            if (!Models.User.CreateAccount(email, pending, Current.Now, null, null, out newUser, out error))
            {
                return Content("<font color='red'>For [" + email + "] and PendingUser.Id = " + pending.Id + " - " + error + "</font>", "text/html");
            }

            // And indicate that this is from ASP.NET Membership
            newUser.PasswordVersion = MembershipCompat.PasswordVersion;
            Current.WriteDB.SubmitChanges();

            return Content("[" + email + "] became <a href='" + newUser.GetClaimedIdentifier() + "'>user</a>", "text/html");
        }
    }
}