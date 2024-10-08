﻿using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using SomeDAO.Backend.Data;
using SomeDAO.Backend.Services;
using Swashbuckle.AspNetCore.Annotations;
using TonLibDotNet;

namespace SomeDAO.Backend.Api
{
    [ApiController]
    [Consumes(System.Net.Mime.MediaTypeNames.Application.Json)]
    [Produces(System.Net.Mime.MediaTypeNames.Application.Json)]
    [Route("/api/[action]")]
    [SwaggerResponse(200, "Request is accepted, processed and response contains requested data.")]
    public class SearchController : ControllerBase
    {
        private const int MinPageSize = 10;
        private const int MaxPageSize = 100;

        private readonly CachedData cachedData;
        private readonly Lazy<IDbProvider> lazyDbProvider;

        public SearchController(CachedData searchService, Lazy<IDbProvider> lazyDbProvider)
        {
            this.cachedData = searchService ?? throw new ArgumentNullException(nameof(searchService));
            this.lazyDbProvider = lazyDbProvider ?? throw new ArgumentNullException(nameof(lazyDbProvider));
        }

        /// <summary>
        /// Returns general configuration data.
        /// </summary>
        [HttpGet]
        public ActionResult<BackendConfig> Config()
        {
            return new BackendConfig
            {
                MasterContractAddress = cachedData.MasterAddress,
                Mainnet = cachedData.InMainnet,
                Categories = cachedData.AllCategories,
                Languages = cachedData.AllLanguages,
            };
        }

        /// <summary>
        /// Returns some statistics.
        /// </summary>
        /// <remarks>
        /// Drill-down lists only display items with a non-zero value.
        /// </remarks>
        [HttpGet]
        public ActionResult<BackendStatistics> Stat()
        {
            return new BackendStatistics
            {
                OrderCount = cachedData.AllOrders.Count,
                OrderCountByStatus = cachedData.OrderCountByStatus,
                OrderCountByCategory = cachedData.OrderCountByCategory,
                OrderCountByLanguage = cachedData.OrderCountByLanguage,
                UserCount = cachedData.AllUsers.Count,
                UserCountByStatus = cachedData.UserCountByStatus,
                UserCountByLanguage = cachedData.UserCountByLanguage,
            };
        }

        /// <summary>
        /// Returns list of ACTIVE (available to work at) Orders that meet filter.
        /// </summary>
        /// <param name="query">Free query</param>
        /// <param name="category">Show only specified category.</param>
        /// <param name="language">Show only specified language.</param>
        /// <param name="minPrice">Minimum price to include</param>
        /// <param name="orderBy">Sort field: 'createdAt' or 'deadline'.</param>
        /// <param name="sort">Sort order: 'asc' or 'desc'.</param>
        /// <param name="translateTo">Language (key or code/name) of language to translate to. Must match one of supported languages (from config).</param>
        /// <param name="page">Page number to return (default 0).</param>
        /// <param name="pageSize">Page size (default 10, max 100).</param>
        /// <remarks>
        /// <para>
        /// With non-empty <b><paramref name="translateTo"/></b> param returned top-level objects (Orders) will have fields <b>nameTranslated</b>, <b>descriptionTranslated</b> and <b>technicalTaskTranslated</b> filled with translated values of their corresponding original field values.
        /// </para>
        /// <para>
        /// These fields may be null if corresponding value is not translated yet.
        /// Also, these fields will be null if original order language is equal to the language to translate to.
        /// </para>
        /// <para>
        /// Expected usage: <code>… = (item.nameTranslated ?? item.name)</code>.
        /// </para>
        /// </remarks>
        [SwaggerResponse(400, "Invalid request.")]
        [HttpGet]
        public ActionResult<List<Order>> Search(
            string? query,
            string? category,
            string? language,
            decimal? minPrice,
            OrderBy orderBy = OrderBy.CreatedAt,
            Sort sort = Sort.Asc,
            string? translateTo = null,
            [Range(0, int.MaxValue)] int page = 0,
            [Range(MinPageSize, MaxPageSize)] int pageSize = MinPageSize)
        {
            var source = cachedData.ActiveOrders;
            if (!string.IsNullOrEmpty(translateTo)
                && !cachedData.ActiveOrdersTranslated.TryGetValue(translateTo, out source))
            {
                ModelState.AddModelError(nameof(translateTo), "Unknown (unsupported) language value");
                return ValidationProblem();
            }

            var list = SearchActiveOrders(source!, query, category, language, minPrice);

            var ordered = (orderBy, sort) switch
            {
                (OrderBy.CreatedAt, Sort.Asc) => list.OrderBy(x => x.CreatedAt),
                (OrderBy.CreatedAt, _) => list.OrderBy(x => x.Deadline),
                (_, Sort.Asc) => list.OrderByDescending(x => x.CreatedAt),
                (_, _) => list.OrderByDescending(x => x.Deadline),
            };

            return ordered.Skip(page * pageSize).Take(pageSize).ToList();
        }

        /// <summary>
        /// Returns number of ACTIVE (available to work at) Orders that meet filter.
        /// </summary>
        /// <param name="query">Free query</param>
        /// <param name="category">Show only specified category.</param>
        /// <param name="language">Show only specified language.</param>
        /// <param name="minPrice">Minimum price to include</param>
        [HttpGet]
        public ActionResult<int> SearchCount(
            string? query,
            string? category,
            string? language,
            decimal? minPrice)
        {
            var list = SearchActiveOrders(cachedData.ActiveOrders, query, category, language, minPrice);

            return list.Count();
        }

        /// <summary>
        /// Find user by wallet address.
        /// </summary>
        /// <param name="address">Address of user's main wallet (in user-friendly form).</param>
        /// <param name="translateTo">Language (key or code/name) of language to translate to. Must match one of supported languages (from config).</param>
        [SwaggerResponse(400, "Address is empty or invalid.")]
        [HttpGet]
        public ActionResult<FindResult<User>> FindUser([Required(AllowEmptyStrings = false)] string address, string? translateTo = null)
        {
            if (string.IsNullOrEmpty(address))
            {
                ModelState.AddModelError(nameof(address), "Address is required.");
                return ValidationProblem();
            }
            else if (!TonUtils.Address.TrySetBounceable(address, false, out address))
            {
                ModelState.AddModelError(nameof(address), "Address not valid (wrong length, contains invalid characters, etc).");
                return ValidationProblem();
            }

            Language? translateLanguage = default;
            if (!string.IsNullOrEmpty(translateTo))
            {
                translateLanguage = cachedData.AllLanguages.Find(x => x.Name == translateTo || x.Hash == translateTo);
                if (translateLanguage == null)
                {
                    ModelState.AddModelError(nameof(translateTo), "Unknown (unsupported) language value");
                return ValidationProblem();
                }
            }

            var user = cachedData.AllUsers.Find(x => StringComparer.Ordinal.Equals(x.UserAddress, address));

            if (user != null && translateLanguage != null && user.AboutHash != null)
            {
                user = user.ShallowCopy();
                var db = lazyDbProvider.Value.MainDb;
                var translated = db.Find<Translation>(x => x.Hash == user.AboutHash && x.Language == translateLanguage.Name);
                user.AboutTranslated = translated?.TranslatedText;
            }

            return new FindResult<User>(user);
        }

        /// <summary>
        /// Get user by index.
        /// </summary>
        /// <param name="index">ID of user ('index' field from user contract).</param>
        /// <param name="translateTo">Language (key or code/name) of language to translate to. Must match one of supported languages (from config).</param>
        [SwaggerResponse(400, "Index is invalid (or user does not exist).")]
        [HttpGet]
        public ActionResult<User> GetUser([Required] long index, string? translateTo = null)
        {
            var user = cachedData.AllUsers.Find(x => x.Index == index);

            if (user == null)
            {
                ModelState.AddModelError(nameof(index), "Invalid index (or user does not exist).");
                return ValidationProblem();
            }

            Language? translateLanguage = default;
            if (!string.IsNullOrEmpty(translateTo))
            {
                translateLanguage = cachedData.AllLanguages.Find(x => x.Name == translateTo || x.Hash == translateTo);
                if (translateLanguage == null)
                {
                    ModelState.AddModelError(nameof(translateTo), "Unknown (unsupported) language value");
                    return ValidationProblem();
                }
            }

            if (translateLanguage != null && user.AboutHash != null)
            {
                user = user.ShallowCopy();
                var db = lazyDbProvider.Value.MainDb;
                var translated = db.Find<Translation>(x => x.Hash == user.AboutHash && x.Language == translateLanguage.Name);
                user.AboutTranslated = translated?.TranslatedText;
            }

            return user;
        }

        /// <summary>
        /// Get order by index.
        /// </summary>
        /// <param name="index">ID of order ('index' field from order contract).</param>
        /// <param name="translateTo">Language (key or code/name) of language to translate to. Must match one of supported languages (from config).</param>
        /// <param name="currentUserIndex">Index of current/active user (to have non-null <see cref="Order.CurrentUserResponse"/> in response).</param>
        [SwaggerResponse(400, "Index is invalid (or order/user does not exist).")]
        [HttpGet]
        public ActionResult<Order> GetOrder([Required] long index, string? translateTo = null, long? currentUserIndex = null)
        {
            var order = cachedData.AllOrders.Find(x => x.Index == index);

            if (order == null)
            {
                ModelState.AddModelError(nameof(index), "Invalid index (or order does not exist).");
                return ValidationProblem();
            }

            var user = currentUserIndex == null ? default : cachedData.AllUsers.Find(x => x.Index == currentUserIndex);
            if (user == null && currentUserIndex != null)
            {
                ModelState.AddModelError(nameof(currentUserIndex), "Invalid index (or user does not exist).");
                return ValidationProblem();
            }

            Language? translateLanguage = default;
            if (!string.IsNullOrEmpty(translateTo))
            {
                translateLanguage = cachedData.AllLanguages.Find(x => x.Name == translateTo || x.Hash == translateTo);
                if (translateLanguage == null)
                {
                    ModelState.AddModelError(nameof(translateTo), "Unknown (unsupported) language value");
                    return ValidationProblem();
                }
            }

            order = order.ShallowCopy();

            if (translateLanguage != null)
            {
                var db = lazyDbProvider.Value.MainDb;

                if (order.NameHash != null)
                {
                    var translated = db.Find<Translation>(x => x.Hash == order.NameHash && x.Language == translateLanguage.Name);
                    order.NameTranslated = translated?.TranslatedText;
                }
                if (order.DescriptionHash != null)
                {
                    var translated = db.Find<Translation>(x => x.Hash == order.DescriptionHash && x.Language == translateLanguage.Name);
                    order.DescriptionTranslated = translated?.TranslatedText;
                }
                if (order.TechnicalTaskHash != null)
                {
                    var translated = db.Find<Translation>(x => x.Hash == order.TechnicalTaskHash && x.Language == translateLanguage.Name);
                    order.TechnicalTaskTranslated = translated?.TranslatedText;
                }
            }

            if (user != null)
            {
                var db = lazyDbProvider.Value.MainDb;
                order.CurrentUserResponse = db.Table<OrderResponse>().FirstOrDefault(x => x.OrderId == order.Id && x.FreelancerAddress == user.UserAddress);
            }

            return order;
        }

        /// <summary>
        /// Find order by contract address.
        /// </summary>
        /// <param name="address">Address of order contract (in user-friendly form).</param>
        /// <param name="translateTo">Language (key or code/name) of language to translate to. Must match one of supported languages (from config).</param>
        [SwaggerResponse(400, "Address is empty or invalid.")]
        [HttpGet]
        public ActionResult<FindResult<Order>> FindOrder([Required(AllowEmptyStrings = false)] string address, string? translateTo = null)
        {
            if (string.IsNullOrEmpty(address))
            {
                ModelState.AddModelError(nameof(address), "Address is required.");
                return ValidationProblem();
            }
            else if (!TonUtils.Address.TrySetBounceable(address, true, out address))
            {
                ModelState.AddModelError(nameof(address), "Address not valid (wrong length, contains invalid characters, etc).");
                return ValidationProblem();
            }

            Language? translateLanguage = default;
            if (!string.IsNullOrEmpty(translateTo))
            {
                translateLanguage = cachedData.AllLanguages.Find(x => x.Name == translateTo || x.Hash == translateTo);
                if (translateLanguage == null)
                {
                    ModelState.AddModelError(nameof(translateTo), "Unknown (unsupported) language value");
                    return ValidationProblem();
                }
            }

            var order = cachedData.AllOrders.Find(x => StringComparer.Ordinal.Equals(x.Address, address));

            if (order != null && translateLanguage != null)
            {
                order = order.ShallowCopy();

                var db = lazyDbProvider.Value.MainDb;

                if (order.NameHash != null)
                {
                    var translated = db.Find<Translation>(x => x.Hash == order.NameHash && x.Language == translateLanguage.Name);
                    order.NameTranslated = translated?.TranslatedText;
                }
                if (order.DescriptionHash != null)
                {
                    var translated = db.Find<Translation>(x => x.Hash == order.DescriptionHash && x.Language == translateLanguage.Name);
                    order.DescriptionTranslated = translated?.TranslatedText;
                }
                if (order.TechnicalTaskHash != null)
                {
                    var translated = db.Find<Translation>(x => x.Hash == order.TechnicalTaskHash && x.Language == translateLanguage.Name);
                    order.TechnicalTaskTranslated = translated?.TranslatedText;
                }
            }

            return new FindResult<Order>(order);
        }

        /// <summary>
        /// Get user statistics - number of orders, detailed by role (customer / freelancer) and by status.
        /// </summary>
        /// <param name="index">ID of user ('index' field from user contract).</param>
        /// <remarks>Only statuses with non-zero number of orders are returned.</remarks>
        [SwaggerResponse(400, "Index is invalid (or user does not exist).")]
        [HttpGet]
        public ActionResult<UserStat> GetUserStats([Required] long index)
        {
            var user = cachedData.AllUsers.Find(x => x.Index == index);

            if (user == null)
            {
                ModelState.AddModelError(nameof(index), "Invalid index (or user does not exist).");
                return ValidationProblem();
            }

            var asCustomer = cachedData.AllOrders.Where(x => StringComparer.Ordinal.Equals(x.CustomerAddress, user.UserAddress));
            var asFreelancer = cachedData.AllOrders.Where(x => StringComparer.Ordinal.Equals(x.FreelancerAddress, user.UserAddress));

            var res = new UserStat()
            {
                AsCustomerByStatus = asCustomer.GroupBy(x => x.Status).ToDictionary(x => x.Key, x => x.Count()),
                AsFreelancerByStatus = asFreelancer.GroupBy(x => x.Status).ToDictionary(x => x.Key, x => x.Count()),
            };

            res.AsCustomerTotal = res.AsCustomerByStatus.Sum(x => x.Value);
            res.AsFreelancerTotal = res.AsFreelancerByStatus.Sum(x => x.Value);

            return res;
        }

        /// <summary>
        /// Get user statistics v2 - number of orders, detailed by 'artificial' status of user in order.
        /// </summary>
        /// <param name="index">ID of user ('index' field from user contract).</param>
        /// <remarks>
        /// <para>Use <see cref="GetUserOrders">GetUserOrders</see> to get list of orders in particular status.</para>
        /// </remarks>
        [SwaggerResponse(400, "Index is invalid (or user does not exist).")]
        [HttpGet]
        public ActionResult<UserStat2> GetUserStats2([Required] long index)
        {
            var user = cachedData.AllUsers.Find(x => x.Index == index);

            if (user == null)
            {
                ModelState.AddModelError(nameof(index), "Invalid index (or user does not exist).");
                return ValidationProblem();
            }

            var orders = cachedData.AllOrders;
            var responses = cachedData.ActiveOrdersUsersResponded;

            var res = new UserStat2();

            var ccnp = System.Text.Json.JsonNamingPolicy.CamelCase;

            foreach (var val in Enum.GetValues<CustomerInOrderStatus>())
            {
                res.AsCustomerByStatus[ccnp.ConvertName(val.ToString())] = FilterOrdersByStatus(orders, user.UserAddress, val).Count();
            }

            foreach(var val in Enum.GetValues<FreelancerInOrderStatus>())
            {
                res.AsFreelancerByStatus[ccnp.ConvertName(val.ToString())] = FilterOrdersByStatus(orders, user.UserAddress, responses, val).Count();
            }

            // completed: orders where freelancer = <user> and: status in (6, 7) or (status=10 and freelancer_part = 100)
            var completed = orders
                .Where(x => StringComparer.Ordinal.Equals(x.FreelancerAddress, user.UserAddress))
                .Where(x => x.Status == Order.status_completed || x.Status == Order.status_payment_forced || (x.Status == Order.status_arbitration_solved && x.ArbitrationFreelancerPart >= 100));
            res.AsFreelancerByStatus[ccnp.ConvertName("CompletedTotal")] = completed.Count();

            // failed: orders where freelancer = <user> and status 5 or (status 10 if freelancer_part < 100)
            var failed = orders
                .Where(x => StringComparer.Ordinal.Equals(x.FreelancerAddress, user.UserAddress))
                .Where(x => x.Status == Order.status_refunded || (x.Status == Order.status_arbitration_solved && x.ArbitrationFreelancerPart < 100));
            res.AsFreelancerByStatus[ccnp.ConvertName("FailedTotal")] = failed.Count();

            return res;
        }

        /// <summary>
        /// Get list of user orders by his 'artificial' status in order.
        /// </summary>
        /// <remarks>
        /// <para>Exactly one of <paramref name="customerStatus"/> and <paramref name="freelancerStatus"/> must be specified.</para>
        /// <para>Use <see cref="GetUserStats2">GetUserStats2</see> to get number of orders in each status.</para>
        /// </remarks>
        /// <param name="index">ID of user ('index' field from user contract).</param>
        /// <param name="customerStatus">Status for orders where user is customer (case-insensitive).</param>
        /// <param name="freelancerStatus">Status for orders where user is freelancer (case-insensitive).</param>
        /// <param name="translateTo">Language (key or code/name) of language to translate to. Must match one of supported languages (from config).</param>
        [SwaggerResponse(400, "Invalid (nonexisting) 'index' or 'role' value.")]
        [HttpGet]
        public ActionResult<List<Order>> GetUserOrders(
            [Required] long index,
            CustomerInOrderStatus? customerStatus,
            FreelancerInOrderStatus? freelancerStatus,
            string? translateTo = null)
        {
            if (customerStatus.HasValue == freelancerStatus.HasValue)
            {
                ModelState.AddModelError(nameof(freelancerStatus), $"Exactly one of '{nameof(customerStatus)}' and '{nameof(freelancerStatus)}' must be set.");
                return ValidationProblem();
            }

            Language? translateLanguage = default;
            if (!string.IsNullOrEmpty(translateTo))
            {
                translateLanguage = cachedData.AllLanguages.Find(x => x.Name == translateTo || x.Hash == translateTo);
                if (translateLanguage == null)
                {
                    ModelState.AddModelError(nameof(translateTo), "Unknown (unsupported) language value");
                    return ValidationProblem();
                }
            }

            var user = cachedData.AllUsers.Find(x => x.Index == index);

            if (user == null)
            {
                ModelState.AddModelError(nameof(index), "Invalid index (or user does not exist).");
                return ValidationProblem();
            }

            var query = customerStatus.HasValue
                ? FilterOrdersByStatus(cachedData.AllOrders, user.UserAddress, customerStatus.Value)
                : FilterOrdersByStatus(cachedData.AllOrders, user.UserAddress, cachedData.ActiveOrdersUsersResponded, freelancerStatus!.Value);

            var list = query.OrderByDescending(x => x.Index).ToList();

            if (translateLanguage != null)
            {
                list = list.Select(x => x.ShallowCopy()).ToList();

                var db = lazyDbProvider.Value.MainDb;

                foreach (var order in list)
                {
                    if (order.NameHash != null)
                    {
                        var translated = db.Find<Translation>(x => x.Hash == order.NameHash && x.Language == translateLanguage.Name);
                        order.NameTranslated = translated?.TranslatedText;
                    }
                    if (order.DescriptionHash != null)
                    {
                        var translated = db.Find<Translation>(x => x.Hash == order.DescriptionHash && x.Language == translateLanguage.Name);
                        order.DescriptionTranslated = translated?.TranslatedText;
                    }
                    if (order.TechnicalTaskHash != null)
                    {
                        var translated = db.Find<Translation>(x => x.Hash == order.TechnicalTaskHash && x.Language == translateLanguage.Name);
                        order.TechnicalTaskTranslated = translated?.TranslatedText;
                    }
                }
            }

            return list;
        }

        /// <summary>
        /// Get list of user activity in different orders.
        /// </summary>
        /// <param name="index">ID of user ('index' field from user contract).</param>
        /// <param name="page">Page number to return (default 0).</param>
        /// <param name="pageSize">Page size (default 10, max 100).</param>
        [SwaggerResponse(400, "Invalid (nonexisting) 'index' value.")]
        [HttpGet]
        public ActionResult<List<OrderActivity>> GetUserActivity(
            [Required] long index,
            [Range(0, int.MaxValue)] int page = 0,
            [Range(MinPageSize, MaxPageSize)] int pageSize = MinPageSize)
        {
            var user = cachedData.AllUsers.Find(x => x.Index == index);

            if (user == null)
            {
                ModelState.AddModelError(nameof(index), "Invalid index (or user does not exist).");
                return ValidationProblem();
            }

            var allOrders = cachedData.AllOrders;
            var db = lazyDbProvider.Value.MainDb;
            var list = db.Table<OrderActivity>()
                .Where(x => x.SenderAddress == user.UserAddress)
                .OrderByDescending(x => x.Timestamp)
                .Skip(page * pageSize)
                .Take(pageSize)
                .ToList();

            foreach (var item in list)
            {
                item.Order = allOrders.Find(x => x.Id == item.OrderId);
            }

            return list;
        }

        /// <summary>
        /// Get list of order activity.
        /// </summary>
        /// <param name="index">ID of order ('index' field from order contract).</param>
        /// <param name="page">Page number to return (default 0).</param>
        /// <param name="pageSize">Page size (default 10, max 100).</param>
        [SwaggerResponse(400, "Invalid (nonexisting) 'index' value.")]
        [HttpGet]
        public ActionResult<List<OrderActivity>> GetOrderActivity(
            [Required] long index,
            [Range(0, int.MaxValue)] int page = 0,
            [Range(MinPageSize, MaxPageSize)] int pageSize = MinPageSize)
        {
            var order = cachedData.AllOrders.Find(x => x.Index == index);

            if (order == null)
            {
                ModelState.AddModelError(nameof(index), "Invalid index (or order does not exist).");
                return ValidationProblem();
            }

            var allUsers = cachedData.AllUsers;
            var db = lazyDbProvider.Value.MainDb;
            var list = db.Table<OrderActivity>()
                .Where(x => x.OrderId == order.Id)
                .OrderByDescending(x => x.Timestamp)
                .Skip(page * pageSize)
                .Take(pageSize)
                .ToList();

            foreach (var item in list)
            {
                item.Sender = allUsers.Find(x => StringComparer.Ordinal.Equals(x.UserAddress, item.SenderAddress));
            }

            return list;
        }

        /// <summary>
        /// Get list of order responses.
        /// </summary>
        /// <param name="index">ID of order ('index' field from order contract).</param>
        /// <remarks>
        /// Responses are sorted by price (high prices first). There can be no more than 255 responses,
        ///   so no paging is used (always all responses are returned), and they may be sorted client-side when needed.
        /// </remarks>
        [SwaggerResponse(400, "Invalid (nonexisting) 'index' value.")]
        [HttpGet]
        public ActionResult<List<OrderResponse>> GetOrderResponses([Required] long index)
        {
            var order = cachedData.AllOrders.Find(x => x.Index == index);

            if (order == null)
            {
                ModelState.AddModelError(nameof(index), "Invalid index (or order does not exist).");
                return ValidationProblem();
            }

            var db = lazyDbProvider.Value.MainDb;
            var list = db.Table<OrderResponse>()
                .Where(x => x.OrderId == order.Id)
                .OrderByDescending(x => x.Price)
                .ToList();

            foreach (var item in list)
            {
                item.Freelancer = cachedData.AllUsers.Find(x => StringComparer.Ordinal.Equals(x.UserAddress, item.FreelancerAddress));
            }

            return list;
        }

        /// <summary>
        /// Get list of orders.
        /// </summary>
        /// <remarks>Translations and inner object are not provided!</remarks>
        /// <param name="status">Status of returned orders.</param>
        /// <param name="category">Category of returned orders.</param>
        /// <param name="language">Language of returned orders.</param>
        /// <param name="sort">Sort order: 'asc' or 'desc'.</param>
        /// <param name="page">Page number to return (default 0).</param>
        /// <param name="pageSize">Page size (default 10, max 100).</param>
        [SwaggerResponse(400, "Invalid request.")]
        [HttpGet]
        public ActionResult<List<Order>> ListOrders(
            int? status,
            string? category,
            string? language,
            Sort sort = Sort.Asc,
            [Range(0, int.MaxValue)] int page = 0,
            [Range(MinPageSize, MaxPageSize)] int pageSize = MinPageSize)
        {
            var list = cachedData.AllOrders
                .Where(x => status == null || x.Status == status)
                .Where(x => string.IsNullOrEmpty(category) || x.Category == category)
                .Where(x => string.IsNullOrEmpty(language) || x.Language == language);

            var sorted = sort == Sort.Asc ? list.OrderBy(x => x.Index) : list.OrderByDescending(x => x.Index);

            return sorted.Skip(page * pageSize).Take(pageSize)
                .Select(x => x.ShallowCopy())
                .Select(x => { x.Customer = null; x.Freelancer = null; return x; })
                .ToList();
        }

        /// <summary>
        /// Get list of users.
        /// </summary>
        /// <remarks>Translations and inner object are not provided!</remarks>
        /// <param name="status">Status of returned users ('active' or 'moderation' or 'banned').</param>
        /// <param name="language">Language of returned users.</param>
        /// <param name="sort">Sort order: 'asc' or 'desc'.</param>
        /// <param name="page">Page number to return (default 0).</param>
        /// <param name="pageSize">Page size (default 10, max 100).</param>
        [SwaggerResponse(400, "Invalid request.")]
        [HttpGet]
        public ActionResult<List<User>> ListUsers(
            UserStatus? status,
            string? language,
            Sort sort = Sort.Asc,
            [Range(0, int.MaxValue)] int page = 0,
            [Range(MinPageSize, MaxPageSize)] int pageSize = MinPageSize)
        {
            var list = cachedData.AllUsers
                .Where(x => status == null || x.UserStatus == status)
                .Where(x => string.IsNullOrEmpty(language) || x.Language == language);

            var sorted = sort == Sort.Asc ? list.OrderBy(x => x.Index) : list.OrderByDescending(x => x.Index);

            return sorted.Skip(page * pageSize).Take(pageSize).ToList();
        }

        protected IEnumerable<Order> SearchActiveOrders(
            List<Order> source,
            string? query,
            string? category,
            string? language,
            decimal? minPrice)
        {
            var list = source.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(category))
            {
                list = list.Where(x => string.Equals(x.Category, category, StringComparison.InvariantCultureIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(language))
            {
                list = list.Where(x => string.Equals(x.Language, language, StringComparison.InvariantCultureIgnoreCase));
            }

            if (minPrice != null)
            {
                list = list.Where(x => x.Price >= minPrice);
            }

            if (!string.IsNullOrWhiteSpace(query))
            {
                var words = query.ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                list = list.Where(x => Array.TrueForAll(words, z => x.TextToSearch.Contains(z, StringComparison.InvariantCulture)));
            }

            return list;
        }

        /// <inheritdoc cref="CustomerInOrderStatus"/>
        protected IEnumerable<Order> FilterOrdersByStatus(IEnumerable<Order> source, string userAddress, CustomerInOrderStatus status)
        {
            var source2 = source.Where(x => StringComparer.Ordinal.Equals(x.CustomerAddress, userAddress));

            return status switch
            {
                CustomerInOrderStatus.OnModeration => source2.Where(x => x.Status == 0),
                CustomerInOrderStatus.NoResponses => source2.Where(x => x.Status == 1 && x.ResponsesCount == 0),
                CustomerInOrderStatus.HaveResponses => source2.Where(x => x.Status == 1 && x.ResponsesCount > 0),
                CustomerInOrderStatus.OfferMade => source2.Where(x => x.Status == 2),
                CustomerInOrderStatus.InTheWork => source2.Where(x => x.Status == 3),
                CustomerInOrderStatus.PendingPayment => source2.Where(x => x.Status == 4),
                CustomerInOrderStatus.Arbitration => source2.Where(x => x.Status == 8 || x.Status == 9),
                CustomerInOrderStatus.Completed => source2.Where(x => x.Status == 6 || x.Status == 5 || x.Status == 7 || x.Status == 10),
                _ => source.Where(x => false),
            };
        }

        /// <inheritdoc cref="FreelancerInOrderStatus"/>
        protected IEnumerable<Order> FilterOrdersByStatus(IEnumerable<Order> source, string userAddress, IDictionary<long, HashSet<string>> responses, FreelancerInOrderStatus status)
        {
            var source2 = source.Where(x => StringComparer.Ordinal.Equals(x.FreelancerAddress, userAddress));

            return status switch
            {
                FreelancerInOrderStatus.ResponseSent => source.Where(x => x.Status == 1 && responses.TryGetValue(x.Index, out var hs) && hs.Contains(userAddress)),
                FreelancerInOrderStatus.ResponseDenied => source.Where(x => x.Status == 2 && !StringComparer.Ordinal.Equals(x.FreelancerAddress, userAddress))
                                                                .Where(x => responses.TryGetValue(x.Index, out var hs) && hs.Contains(userAddress)),
                FreelancerInOrderStatus.AnOfferCameIn => source2.Where(x => x.Status == 2),
                FreelancerInOrderStatus.InTheWork => source2.Where(x => x.Status == 3),
                FreelancerInOrderStatus.OnInspection => source2.Where(x => x.Status == 4),
                FreelancerInOrderStatus.Arbitration => source2.Where(x => x.Status == 8 || x.Status == 9),
                FreelancerInOrderStatus.Terminated => source2.Where(x => x.Status == 6 || x.Status == 5 || x.Status == 7 || x.Status == 10),
                _ => source.Where(x => false),
            };
        }

        public class BackendConfig
        {
            public string MasterContractAddress { get; set; } = string.Empty;

            public bool Mainnet { get; set; }

            public List<Category> Categories { get; set; } = [];

            public List<Language> Languages { get; set; } = [];
        }

        public class BackendStatistics
        {
            public int OrderCount { get; set; }

            public Dictionary<int, int> OrderCountByStatus { get; set; } = [];

            public Dictionary<string, int> OrderCountByCategory { get; set; } = [];

            public Dictionary<string, int> OrderCountByLanguage { get; set; } = [];

            public int UserCount { get; set; }

            public Dictionary<UserStatus, int> UserCountByStatus { get; set; } = [];

            public Dictionary<string, int> UserCountByLanguage { get; set; } = [];
        }

        public class UserStat
        {
            public int AsCustomerTotal { get; set; }

            public Dictionary<int, int> AsCustomerByStatus { get; set; } = [];

            public int AsFreelancerTotal { get; set; }

            public Dictionary<int, int> AsFreelancerByStatus { get; set; } = [];
        }

        public class UserStat2
        {
            public Dictionary<string, int> AsCustomerByStatus { get; set; } = [];

            public Dictionary<string, int> AsFreelancerByStatus { get; set; } = [];
        }

        public class FindResult<T>
            where T : class
        {
            public FindResult(T? data)
            {
                Found = data != null;
                Data = data;
            }

            public bool Found { get; set; }

            public T? Data { get; set; }
        }
    }
}
