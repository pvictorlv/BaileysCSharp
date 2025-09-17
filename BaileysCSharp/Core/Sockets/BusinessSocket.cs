using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using BaileysCSharp.Core.Models;
using BaileysCSharp.Core.Models.Business;
using BaileysCSharp.Core.Utils;
using BaileysCSharp.Core.WABinary;
using static BaileysCSharp.Core.Utils.GenericUtils;
using static BaileysCSharp.Core.WABinary.Constants;

namespace BaileysCSharp.Core.Sockets
{
    public abstract class BusinessSocket : MessagesRecvSocket
    {
        public BusinessSocket([NotNull] SocketConfig config) : base(config)
        {
        }

        /// <summary>
        /// Update business profile information
        /// </summary>
        public async Task<BinaryNode> UpdateBusinessProfile(UpdateBusinessProfileProps options)
        {
            var node = new BinaryNode
            {
                tag = "iq",
                attrs = new Dictionary<string, string>
                {
                    { "id", GenerateMessageTag() },
                    { "to", S_WHATSAPP_NET },
                    { "type", "set" },
                    { "xmlns", "w:biz" }
                },
                content = new BinaryNode[]
                {
                    new BinaryNode
                    {
                        tag = "business_profile",
                        attrs = new Dictionary<string, string>
                        {
                            { "v", "3" },
                            { "mutation_type", "delta" }
                        },
                        content = BuildBusinessProfileContent(options).ToArray()
                    }
                }
            };

            return await Query(node);
        }

        /// <summary>
        /// Upload and update cover photo for business profile
        /// </summary>
        public async Task<string> UpdateCoverPhoto(byte[] photoData)
        {
            var mediaUploadData = await GetRawMediaUploadData(photoData, "biz-cover-photo");
            var fileSha256B64 = Convert.ToBase64String(mediaUploadData.FileSha256);

            var uploadResult = await WaUploadToServer(mediaUploadData.FilePath, new MediaUploadOptions
            {
                FileEncSha256B64 = fileSha256B64,
                MediaType = "biz-cover-photo"
            });

            var node = new BinaryNode
            {
                tag = "iq",
                attrs = new Dictionary<string, string>
                {
                    { "id", GenerateMessageTag() },
                    { "to", S_WHATSAPP_NET },
                    { "type", "set" },
                    { "xmlns", "w:biz" }
                },
                content = new BinaryNode[]
                {
                    new BinaryNode
                    {
                        tag = "business_profile",
                        attrs = new Dictionary<string, string>
                        {
                            { "v", "3" },
                            { "mutation_type", "delta" }
                        },
                        content = new BinaryNode[]
                        {
                            new BinaryNode
                            {
                                tag = "cover_photo",
                                attrs = new Dictionary<string, string>
                                {
                                    { "id", uploadResult.Fbid.ToString() },
                                    { "op", "update" },
                                    { "token", uploadResult.MetaHmac },
                                    { "ts", uploadResult.Timestamp.ToString() }
                                }
                            }
                        }
                    }
                }
            };

            await Query(node);
            return uploadResult.Fbid.ToString();
        }

        /// <summary>
        /// Remove cover photo from business profile
        /// </summary>
        public async Task<bool> RemoveCoverPhoto(string photoId)
        {
            var node = new BinaryNode
            {
                tag = "iq",
                attrs = new Dictionary<string, string>
                {
                    { "id", GenerateMessageTag() },
                    { "to", S_WHATSAPP_NET },
                    { "type", "set" },
                    { "xmlns", "w:biz" }
                },
                content = new BinaryNode[]
                {
                    new BinaryNode
                    {
                        tag = "business_profile",
                        attrs = new Dictionary<string, string>
                        {
                            { "v", "3" },
                            { "mutation_type", "delta" }
                        },
                        content = new BinaryNode[]
                        {
                            new BinaryNode
                            {
                                tag = "cover_photo",
                                attrs = new Dictionary<string, string>
                                {
                                    { "id", photoId },
                                    { "op", "remove" }
                                }
                            }
                        }
                    }
                }
            };

            await Query(node);
            return true;
        }

        /// <summary>
        /// Get business catalog with products
        /// </summary>
        public async Task<Catalog> GetCatalog(GetCatalogOptions? options = null)
        {
            var node = new BinaryNode
            {
                tag = "iq",
                attrs = new Dictionary<string, string>
                {
                    { "id", GenerateMessageTag() },
                    { "to", S_WHATSAPP_NET },
                    { "type", "get" },
                    { "xmlns", "w:biz:catalog" }
                }
            };

            if (options != null)
            {
                var content = new List<BinaryNode>();
                
                if (options.Limit.HasValue)
                {
                    content.Add(new BinaryNode
                    {
                        tag = "limit",
                        attrs = new Dictionary<string, string>
                        {
                            { "count", options.Limit.ToString() }
                        }
                    });
                }

                if (!string.IsNullOrEmpty(options.Cursor))
                {
                    content.Add(new BinaryNode
                    {
                        tag = "cursor",
                        attrs = new Dictionary<string, string>
                        {
                            { "cursor", options.Cursor }
                        }
                    });
                }

                if (options.IncludeHidden.HasValue)
                {
                    content.Add(new BinaryNode
                    {
                        tag = "include_hidden",
                        attrs = new Dictionary<string, string>
                        {
                            { "value", options.IncludeHidden.Value.ToString().ToLower() }
                        }
                    });
                }

                node.content = content.ToArray();
            }

            var result = await Query(node);
            return ParseCatalogNode(result);
        }

        /// <summary>
        /// Get product collections
        /// </summary>
        public async Task<List<ProductCollection>> GetCollections()
        {
            var node = new BinaryNode
            {
                tag = "iq",
                attrs = new Dictionary<string, string>
                {
                    { "id", GenerateMessageTag() },
                    { "to", S_WHATSAPP_NET },
                    { "type", "get" },
                    { "xmlns", "w:biz:catalog" }
                },
                content = new BinaryNode[]
                {
                    new BinaryNode
                    {
                        tag = "collections"
                    }
                }
            };

            var result = await Query(node);
            return ParseCollectionsNode(result);
        }

        /// <summary>
        /// Create a new product
        /// </summary>
        public async Task<Product> CreateProduct(ProductCreateOptions options)
        {
            var productNode = new BinaryNode
            {
                tag = "product",
                attrs = new Dictionary<string, string>
                {
                    { "name", options.Name }
                }
            };

            var content = new List<BinaryNode> { productNode };

            if (!string.IsNullOrEmpty(options.Description))
            {
                content.Add(new BinaryNode
                {
                    tag = "description",
                    content = options.Description
                });
            }

            if (!string.IsNullOrEmpty(options.ImageUrl))
            {
                content.Add(new BinaryNode
                {
                    tag = "image_url",
                    content = options.ImageUrl
                });
            }

            if (options.Price.HasValue)
            {
                content.Add(new BinaryNode
                {
                    tag = "price",
                    attrs = new Dictionary<string, string>
                    {
                        { "amount", options.Price.Value.ToString() },
                        { "currency", options.Currency ?? "USD" }
                    }
                });
            }

            if (!string.IsNullOrEmpty(options.Url))
            {
                content.Add(new BinaryNode
                {
                    tag = "url",
                    content = options.Url
                });
            }

            if (options.IsHidden.HasValue)
            {
                content.Add(new BinaryNode
                {
                    tag = "is_hidden",
                    content = options.IsHidden.Value.ToString().ToLower()
                });
            }

            if (options.CommodityCode.HasValue)
            {
                content.Add(new BinaryNode
                {
                    tag = "commodity_code",
                    content = options.CommodityCode.Value.ToString()
                });
            }

            var node = new BinaryNode
            {
                tag = "iq",
                attrs = new Dictionary<string, string>
                {
                    { "id", GenerateMessageTag() },
                    { "to", S_WHATSAPP_NET },
                    { "type", "set" },
                    { "xmlns", "w:biz:catalog" }
                },
                content = content.ToArray()
            };

            var result = await Query(node);
            return ParseProductNode(result);
        }

        /// <summary>
        /// Update an existing product
        /// </summary>
        public async Task<Product> UpdateProduct(ProductUpdateOptions options)
        {
            var productNode = new BinaryNode
            {
                tag = "product",
                attrs = new Dictionary<string, string>
                {
                    { "product_id", options.ProductId }
                }
            };

            var content = new List<BinaryNode> { productNode };

            if (!string.IsNullOrEmpty(options.Name))
            {
                content.Add(new BinaryNode
                {
                    tag = "name",
                    content = options.Name
                });
            }

            if (!string.IsNullOrEmpty(options.Description))
            {
                content.Add(new BinaryNode
                {
                    tag = "description",
                    content = options.Description
                });
            }

            if (!string.IsNullOrEmpty(options.ImageUrl))
            {
                content.Add(new BinaryNode
                {
                    tag = "image_url",
                    content = options.ImageUrl
                });
            }

            if (options.Price.HasValue)
            {
                content.Add(new BinaryNode
                {
                    tag = "price",
                    attrs = new Dictionary<string, string>
                    {
                        { "amount", options.Price.Value.ToString() },
                        { "currency", options.Currency ?? "USD" }
                    }
                });
            }

            if (!string.IsNullOrEmpty(options.Url))
            {
                content.Add(new BinaryNode
                {
                    tag = "url",
                    content = options.Url
                });
            }

            if (options.IsHidden.HasValue)
            {
                content.Add(new BinaryNode
                {
                    tag = "is_hidden",
                    content = options.IsHidden.Value.ToString().ToLower()
                });
            }

            if (options.CommodityCode.HasValue)
            {
                content.Add(new BinaryNode
                {
                    tag = "commodity_code",
                    content = options.CommodityCode.Value.ToString()
                });
            }

            var node = new BinaryNode
            {
                tag = "iq",
                attrs = new Dictionary<string, string>
                {
                    { "id", GenerateMessageTag() },
                    { "to", S_WHATSAPP_NET },
                    { "type", "set" },
                    { "xmlns", "w:biz:catalog" }
                },
                content = content.ToArray()
            };

            var result = await Query(node);
            return ParseProductNode(result);
        }

        /// <summary>
        /// Delete a product
        /// </summary>
        public async Task<bool> DeleteProduct(string productId)
        {
            var node = new BinaryNode
            {
                tag = "iq",
                attrs = new Dictionary<string, string>
                {
                    { "id", GenerateMessageTag() },
                    { "to", S_WHATSAPP_NET },
                    { "type", "set" },
                    { "xmlns", "w:biz:catalog" }
                },
                content = new BinaryNode[]
                {
                    new BinaryNode
                    {
                        tag = "product",
                        attrs = new Dictionary<string, string>
                        {
                            { "product_id", productId },
                            { "op", "delete" }
                        }
                    }
                }
            };

            await Query(node);
            return true;
        }

        /// <summary>
        /// Get order details
        /// </summary>
        public async Task<Order> GetOrderDetails(string orderId)
        {
            var node = new BinaryNode
            {
                tag = "iq",
                attrs = new Dictionary<string, string>
                {
                    { "id", GenerateMessageTag() },
                    { "to", S_WHATSAPP_NET },
                    { "type", "get" },
                    { "xmlns", "w:biz:orders" }
                },
                content = new BinaryNode[]
                {
                    new BinaryNode
                    {
                        tag = "order",
                        attrs = new Dictionary<string, string>
                        {
                            { "order_id", orderId }
                        }
                    }
                }
            };

            var result = await Query(node);
            return ParseOrderNode(result);
        }

        private List<BinaryNode> BuildBusinessProfileContent(UpdateBusinessProfileProps options)
        {
            var content = new List<BinaryNode>();

            // Simple fields
            var simpleFields = new[] { "address", "email", "description" };
            foreach (var field in simpleFields)
            {
                var value = field switch
                {
                    "address" => options.Address,
                    "email" => options.Email,
                    "description" => options.Description,
                    _ => null
                };

                if (!string.IsNullOrEmpty(value))
                {
                    content.Add(new BinaryNode
                    {
                        tag = field,
                        content = value
                    });
                }
            }

            // Websites
            if (options.Websites != null && options.Websites.Any())
            {
                content.AddRange(options.Websites.Select(website => new BinaryNode
                {
                    tag = "website",
                    content = website
                }));
            }

            // Business hours
            if (options.Hours != null)
            {
                var hoursNode = new BinaryNode
                {
                    tag = "business_hours",
                    attrs = new Dictionary<string, string>()
                };

                if (!string.IsNullOrEmpty(options.Hours.Timezone))
                {
                    hoursNode.attrs["timezone"] = options.Hours.Timezone;
                }

                hoursNode.content = options.Hours.Config.Select(config => new BinaryNode
                {
                    tag = "business_hours_config",
                    attrs = new Dictionary<string, string>
                    {
                        { "day_of_week", config.DayOfWeek },
                        { "mode", config.Mode }
                    },
                    content = config.Mode == "specific_hours" ? new BinaryNode[]
                    {
                        new BinaryNode
                        {
                            tag = "open_time",
                            attrs = new Dictionary<string, string>
                            {
                                { "minutes", config.OpenTimeInMinutes?.ToString() ?? "0" }
                            }
                        },
                        new BinaryNode
                        {
                            tag = "close_time",
                            attrs = new Dictionary<string, string>
                            {
                                { "minutes", config.CloseTimeInMinutes?.ToString() ?? "0" }
                            }
                        }
                    } : null
                }).ToArray();

                content.Add(hoursNode);
            }

            return content;
        }

        private Catalog ParseCatalogNode(BinaryNode node)
        {
            var catalogNode = GetBinaryNodeChild(node, "catalog");
            if (catalogNode == null) return new Catalog();

            var catalog = new Catalog
            {
                Id = catalogNode.getattr("id"),
                Name = catalogNode.getattr("name"),
                ProductCount = int.TryParse(catalogNode.getattr("product_count"), out var count) ? count : null,
                LastUpdated = DateTime.TryParse(catalogNode.getattr("last_updated"), out var updated) ? updated : null
            };

            // Parse products
            var productNodes = GetBinaryNodeChildren(catalogNode, "product");
            catalog.Products = productNodes.Select(ParseProductFromNode).ToList();

            // Parse collections
            var collectionNodes = GetBinaryNodeChildren(catalogNode, "collection");
            catalog.Collections = collectionNodes.Select(ParseCollectionFromNode).ToList();

            return catalog;
        }

        private Product ParseProductFromNode(BinaryNode node)
        {
            return new Product
            {
                Id = node.getattr("id"),
                Name = node.getattr("name"),
                Description = GetBinaryNodeChildString(node, "description"),
                RetailerId = node.getattr("retailer_id"),
                Url = GetBinaryNodeChildString(node, "url"),
                ImageUrl = GetBinaryNodeChildString(node, "image_url"),
                Price = decimal.TryParse(node.getattr("price"), out var price) ? price : null,
                Currency = node.getattr("currency"),
                IsHidden = bool.TryParse(node.getattr("is_hidden"), out var hidden) ? hidden : null,
                CommodityCode = int.TryParse(node.getattr("commodity_code"), out var code) ? code : null,
                ProductId = node.getattr("product_id")
            };
        }

        private ProductCollection ParseCollectionFromNode(BinaryNode node)
        {
            var collection = new ProductCollection
            {
                Id = node.getattr("id"),
                Name = node.getattr("name"),
                ProductCount = int.TryParse(node.getattr("product_count"), out var count) ? count : null
            };

            var productNodes = GetBinaryNodeChildren(node, "product_id");
            collection.ProductIds = productNodes.Select(p => p.content?.ToString() ?? string.Empty).ToList();

            return collection;
        }

        private List<ProductCollection> ParseCollectionsNode(BinaryNode node)
        {
            var collectionsNode = GetBinaryNodeChild(node, "collections");
            if (collectionsNode == null) return new List<ProductCollection>();

            var collectionNodes = GetBinaryNodeChildren(collectionsNode, "collection");
            return collectionNodes.Select(ParseCollectionFromNode).ToList();
        }

        private Product ParseProductNode(BinaryNode node)
        {
            var productNode = GetBinaryNodeChild(node, "product");
            return productNode != null ? ParseProductFromNode(productNode) : new Product();
        }

        private Order ParseOrderNode(BinaryNode node)
        {
            var orderNode = GetBinaryNodeChild(node, "order");
            if (orderNode == null) return new Order();

            return new Order
            {
                Id = orderNode.getattr("id"),
                ProductId = orderNode.getattr("product_id"),
                ProductName = orderNode.getattr("product_name"),
                Quantity = int.TryParse(orderNode.getattr("quantity"), out var qty) ? qty : null,
                TotalAmount = decimal.TryParse(orderNode.getattr("total_amount"), out var amount) ? amount : null,
                Currency = orderNode.getattr("currency"),
                Status = orderNode.getattr("status"),
                CustomerId = orderNode.getattr("customer_id"),
                CustomerName = orderNode.getattr("customer_name"),
                OrderDate = DateTime.TryParse(orderNode.getattr("order_date"), out var date) ? date : null
            };
        }
    }
}
