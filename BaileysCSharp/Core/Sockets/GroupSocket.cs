using System.Diagnostics.CodeAnalysis;
using BaileysCSharp.Core.Models;
using static BaileysCSharp.Core.Utils.GenericUtils;
using BaileysCSharp.Core.Utils;
using BaileysCSharp.Core.Extensions;
using System.Text;
using BaileysCSharp.Core.WABinary;

namespace BaileysCSharp.Core.Sockets
{
    public enum ParticipantAction
    {
        Add = 1,
        Remove = 2,
        Promote = 3,
        Demote = 4
    }
    public enum GroupSetting
    {
        Announcement = 1,
        Not_Announcement = 2,
        Locked = 3,
        Unlocked = 4
    }
    public enum MemberAddMode
    {
        Admin_Add = 1,
        All_Member_Add = 2,
    }
    public enum MembershipApprovalMode
    {
        On = 1,
        Off = 2,
    }

    public abstract class GroupSocket : ChatSocket
    {
        public GroupSocket([NotNull] SocketConfig config) : base(config)
        {

        }


        protected override async Task<bool> HandleDirtyUpdate(BinaryNode node)
        {
            var dirtyNode = GetBinaryNodeChild(node, "dirty");
            if (dirtyNode?.getattr("type") == "groups")
            {
                await GroupFetchAllParticipating();
                await CleanDirtyBits("groups");
                return true;
            }
            else
            {
                return await base.HandleDirtyUpdate(node);
            }
        }

        private async Task GroupFetchAllParticipating()
        {
            var result = await Query(new BinaryNode
            {
                tag = "iq",
                attrs = new Dictionary<string, string>
                {
                    { "to", "@g.us" },
                    { "xmlns", "w:g2" },
                    { "type", "get" }
                },
                content = new BinaryNode[]
                {
                    new BinaryNode
                    {
                        tag = "participating",
                        attrs = new Dictionary<string, string>(),
                        content = new BinaryNode[]
                        {
                            new BinaryNode { tag = "participants", attrs = new Dictionary<string, string>() },
                            new BinaryNode { tag = "description", attrs = new Dictionary<string, string>() }
                        }
                    }
                }
            });

            var data = new Dictionary<string, GroupMetadataModel>();
            var communitiesChild = GetBinaryNodeChild(result, "communities");
            
            if (communitiesChild != null)
            {
                var communities = GetBinaryNodeChildren(communitiesChild, "community");
                foreach (var communityNode in communities)
                {
                    var metadata = ExtractGroupMetaData(new BinaryNode
                    {
                        tag = "result",
                        attrs = new Dictionary<string, string>(),
                        content = new BinaryNode[] { communityNode }
                    });
                    
                    data[metadata.ID] = metadata;
                }
            }

            // Emit groups.update event with all fetched groups
            EV.Emit(Events.EmitType.GroupsUpdate, data.Values.ToArray());
        }


        public async Task<BinaryNode> GroupQuery(string jid, string type, BinaryNode[] content)
        {
            var node = new BinaryNode()
            {
                tag = "iq",
                attrs = {
                    { "type",type},
                    {"xmlns","w:g2" },
                    {"to",jid},
                },
                content = content
            };

            return await Query(node);
        }

        public async Task<GroupMetadataModel> GroupMetaData(string jid)
        {
            var result = await GroupQuery(jid, "get", [new BinaryNode()
            {
                tag = "query",
                attrs = {
                    {"request","interactive" }
                }
            }]);

            return ExtractGroupMetaData(result);
        }

        //groupCreate
        public async Task<GroupMetadataModel> GroupCreate(string subject, string[] participants)
        {
            var key = GenerateMessageID();
            var result = await GroupQuery("@g.us", "set", [new BinaryNode()
            {
                tag = "create",
                attrs = {
                    {"subject",subject },
                    {"key",key }
                },
                content = participants.Select(x =>
                    new BinaryNode()
                    {
                        tag = "participant",
                        attrs = { {"jid",x } }
                    }
                ).ToArray()
            }]);

            var metaData = ExtractGroupMetaData(result);
            Store.AddGroup(new ContactModel()
            {
                ID = metaData.ID,
                Name = subject
            });

            return metaData;
        }

        //groupLeave
        public async Task GroupLeave(string id)
        {
            var result = await GroupQuery("@g.us", "set", [new BinaryNode()
            {
                tag = "leave",
                attrs = {
                },
                content = new BinaryNode[]
                {
                    new BinaryNode()
                    {
                        tag ="group",
                        attrs = {
                            {"id", id }
                        }
                    }
                }
            }]);
        }
        //groupUpdateSubject
        public async Task GroupUpdateSubject(string jid, string subject)
        {
            var result = await GroupQuery(jid, "set", [new BinaryNode()
            {
                tag = "subject",
                content = Encoding.UTF8.GetBytes(subject)
            }]);
        }

        //groupRequestParticipantsList
        public async Task<GroupMetadataModel> GroupRequestParticipantsList(string jid)
        {
            var result = await GroupQuery(jid, "set", [new BinaryNode()
            {
                tag = "membership_approval_requests",
                attrs = { }
            }]);

            var node = GetBinaryNodeChild(result, "membership_approval_requests");
            var participant = GetBinaryNodeChild(node, "membership_approval_request");

            //This needs to be tested

            return ExtractGroupMetaData(result);
        }
        //groupRequestParticipantsUpdate
        public async Task GroupRequestParticipantsUpdate(string jid, string[] participants, string action)
        {
            var result = await GroupQuery(jid, "set", [new BinaryNode()
            {
                tag = "membership_requests_action",
                content = new BinaryNode[]{
                    new BinaryNode(){
                        tag = action,
                        content = participants.Select(x =>
                            new BinaryNode()
                            {
                                tag = "participant",
                                attrs = {
                                    {"jid",x }
                                }
                            }
                        ).ToArray()
                    }
                }
            }]);

            var node = GetBinaryNodeChild(result, "membership_requests_action");
            var nodeAction = GetBinaryNodeChild(node, action);
            var participantsAffected = GetBinaryNodeChildren(nodeAction, "participant");

            //return participantsAffected.map(p => {
            //    return { status: p.attrs.error || '200', jid: p.attrs.jid }
            //})
        }
        //groupParticipantsUpdate


        public async Task GroupParticipantsUpdate(string jid, string[] participants, ParticipantAction action)
        {
            var result = await GroupQuery(jid, "set", [new BinaryNode()
            {
                tag = action.ToString().ToLower(),
                attrs = {},
                content =  participants.Select(x =>
                            new BinaryNode()
                            {
                                tag = "participant",
                                attrs = {
                                    {"jid",x }
                                }
                            }
                        ).ToArray()
            }]);

            var node = GetBinaryNodeChild(result, action.ToString().ToLower());
            var participantsAffected = GetBinaryNodeChildren(node, "participant");

        }
        //groupUpdateDescription
        public async Task GroupUpdateDescription(string jid, string description)
        {
            var metadata = await GroupMetaData(jid);
            var prev = metadata?.Desc ?? "";

            Dictionary<string, string> attrs = new Dictionary<string, string>();

            if (string.IsNullOrWhiteSpace(description))
            {
                attrs["delete"] = "true";
            }
            else
            {
                attrs["id"] = GenerateMessageID();
            }
            if (!string.IsNullOrWhiteSpace(prev))
            {
                attrs["prev"] = prev;
            }


            var node = new BinaryNode()
            {
                tag = "description",
                attrs = attrs,
            };


            if (!string.IsNullOrWhiteSpace(description))
            {
                node.content = new BinaryNode[] {
                    new BinaryNode()
                    {
                        tag = "body",
                        content = Encoding.UTF8.GetBytes(description)
                    }
                };
            }

            var result = await GroupQuery(jid, "set", [node]);
        }

        //groupInviteCode
        public async Task<string> GroupInviteCode(string jid)
        {
            var result = await GroupQuery(jid, "get", [new BinaryNode() { tag = "invite", }]);
            var inviteNode = GetBinaryNodeChild(result, "invite");
            return inviteNode?.getattr("code") ?? "";
        }
        //groupRevokeInvite
        public async Task<string> GroupRevokeInvite(string jid)
        {
            var result = await GroupQuery(jid, "set", [new BinaryNode() { tag = "invite", }]);
            var inviteNode = GetBinaryNodeChild(result, "invite");
            return inviteNode?.getattr("code") ?? "";
        }
        //groupAcceptInvite
        public async Task<string> GroupAcceptInvite(string code)
        {
            var result = await GroupQuery("@g.us", "set", [new BinaryNode() { tag = "invite", attrs = { { "code", code } } }]);
            var inviteNode = GetBinaryNodeChild(result, "invite");
            return inviteNode?.getattr("code") ?? "";
        }
        //groupAcceptInviteV4
        //groupGetInviteInfo
        public async Task<GroupMetadataModel> GroupGetInviteInfo(string code)
        {
            var result = await GroupQuery("@g.us", "get", [new BinaryNode() { tag = "invite", attrs = { { "code", code } } }]);
            return ExtractGroupMetaData(result);
        }
        //groupToggleEphemeral
        public async Task GroupToggleEphemeral(string jid, ulong ephemeralExpiration = 0)
        {
            BinaryNode node;
            if (ephemeralExpiration > 0)
            {
                node = new BinaryNode()
                {
                    tag = "ephemeral",
                    attrs = { { "expiration", ephemeralExpiration.ToString() } }
                };
            }
            else
            {
                node = new BinaryNode()
                {
                    tag = "not_ephemeral",
                };
            }
            var result = await GroupQuery(jid, "set", [node]);
        }
        //groupSettingUpdate
        public async Task GroupSettingUpdate(string jid, GroupSetting setting)
        {
            await GroupQuery(jid, "set", [new BinaryNode() { tag = setting.ToString().ToLower() }]);
        }
        //groupMemberAddMode
        public async Task GroupMemberAddMode(string jid, MemberAddMode mode)
        {
            await GroupQuery(jid, "set", [new BinaryNode()
            {
                tag = "member_add_mode",
                content = Encoding.UTF8.GetBytes(mode.ToString())
            }]);
        }

        //groupJoinApprovalMode
        public async Task GroupJoinApprovalMode(string jid, MembershipApprovalMode mode)
        {
            await GroupQuery(jid, "set", [new BinaryNode()
            {
                tag = "membership_approval_mode",
                content = new BinaryNode[]{
                    new BinaryNode()
                    {
                        tag = "group_join",
                        attrs = {{"state",mode.ToString().ToLower()}}
                    }
                }
            }]);
        }



        public GroupMetadataModel ExtractGroupMetaData(BinaryNode result)
        {
            var group = GetBinaryNodeChild(result, "group");
            var descChild = GetBinaryNodeChild(result, "description");
            string desc = "";
            string descId = "";
            if (descChild != null)
            {
                desc = GetBinaryNodeChildString(descChild, "body");
                descId = descChild.attrs["id"];
            }


            var groupId = group.attrs["id"].Contains("@") ? group.attrs["id"] : JidUtils.JidEncode(group.attrs["id"], "g.us");
            var eph = GetBinaryNodeChild(group, "ephemeral")?.attrs["expiration"].ToUInt64();

            var participants = GetBinaryNodeChildren(group, "participant");
            var memberAddMode = GetBinaryNodeChildString(group, "member_add_mode") == "all_member_add";

            var metadata = new GroupMetadataModel
            {
                ID = groupId,
                Subject = group.getattr("subject"),
                SubjectOwner = group.getattr("s_o"),
                SubjectTime = group.getattr("s_t").ToUInt64(),
                Size = (ulong)participants.Length,
                Creation = group.attrs["creation"].ToUInt64(),
                Owner = group.getattr("creator") != null ? JidUtils.JidNormalizedUser(group.attrs["creator"]) : null,
                Desc = desc,
                DescID = descId,
                Restrict = GetBinaryNodeChild(group, "locked") != null,
                Announce = GetBinaryNodeChild(group, "announcement") != null,
                IsCommunity = GetBinaryNodeChild(group, "parent") != null,
                IsCommunityAnnounce = GetBinaryNodeChild(group, "default_sub_group") != null,
                JoinApprovalMode = GetBinaryNodeChild(group, "membership_approval_mode") != null,
                MemberAddMode = memberAddMode,
                Participants = participants.Select(x => new GroupParticipantModel()
                {
                    ID = x.attrs["jid"],
                    ParticipantType = x.getattr("type")

                }).ToArray(),
                EphemeralDuration = eph
            };


            return metadata;
        }





        public List<ContactModel> GetAllGroups()
        {
            return Store.GetAllGroups();
        }

        #region Community Features

        /// <summary>
        /// Create a community (parent group)
        /// </summary>
        public async Task<GroupMetadataModel> CommunityCreate(string subject, string description, string[] participants)
        {
            var key = GenerateMessageID();
            var result = await GroupQuery("@g.us", "set", new BinaryNode[]
            {
                new BinaryNode
                {
                    tag = "create",
                    attrs = new Dictionary<string, string>
                    {
                        { "subject", subject },
                        { "key", key },
                        { "community", "true" }
                    },
                    content = new BinaryNode[]
                    {
                        new BinaryNode
                        {
                            tag = "description",
                            attrs = new Dictionary<string, string>
                            {
                                { "id", GenerateMessageID() }
                            },
                            content = new BinaryNode[]
                            {
                                new BinaryNode
                                {
                                    tag = "body",
                                    content = description
                                }
                            }
                        }
                    }.Concat(participants.Select(p => new BinaryNode
                    {
                        tag = "participant",
                        attrs = new Dictionary<string, string> { { "jid", p } }
                    })).ToArray()
                }
            });

            var metadata = ExtractGroupMetaData(result);
            Store.AddGroup(new ContactModel
            {
                ID = metadata.ID,
                Name = subject
            });

            return metadata;
        }

        /// <summary>
        /// Link a subgroup to a community
        /// </summary>
        public async Task<bool> CommunityLinkSubgroup(string communityJid, string subgroupJid)
        {
            var result = await GroupQuery(communityJid, "set", new BinaryNode[]
            {
                new BinaryNode
                {
                    tag = "subgroup",
                    attrs = new Dictionary<string, string>
                    {
                        { "action", "link" },
                        { "subgroup_jid", subgroupJid }
                    }
                }
            });

            return GetBinaryNodeChild(result, "subgroup") != null;
        }

        /// <summary>
        /// Unlink a subgroup from a community
        /// </summary>
        public async Task<bool> CommunityUnlinkSubgroup(string communityJid, string subgroupJid)
        {
            var result = await GroupQuery(communityJid, "set", new BinaryNode[]
            {
                new BinaryNode
                {
                    tag = "subgroup",
                    attrs = new Dictionary<string, string>
                    {
                        { "action", "unlink" },
                        { "subgroup_jid", subgroupJid }
                    }
                }
            });

            return GetBinaryNodeChild(result, "subgroup") != null;
        }

        /// <summary>
        /// Get community metadata including linked subgroups
        /// </summary>
        public async Task<GroupMetadataModel> CommunityMetadata(string jid)
        {
            var result = await GroupQuery(jid, "get", new BinaryNode[]
            {
                new BinaryNode
                {
                    tag = "query",
                    attrs = new Dictionary<string, string>
                    {
                        { "request", "interactive" },
                        { "include_subgroups", "true" }
                    }
                }
            });

            return ExtractGroupMetaData(result);
        }

        /// <summary>
        /// Get all linked subgroups for a community
        /// </summary>
        public async Task<List<GroupMetadataModel>> CommunityGetSubgroups(string communityJid)
        {
            var result = await GroupQuery(communityJid, "get", new BinaryNode[]
            {
                new BinaryNode
                {
                    tag = "subgroups"
                }
            });

            var subgroupsNode = GetBinaryNodeChild(result, "subgroups");
            if (subgroupsNode == null) return new List<GroupMetadataModel>();

            var subgroupNodes = GetBinaryNodeChildren(subgroupsNode, "subgroup");
            var subgroups = new List<GroupMetadataModel>();

            foreach (var subgroupNode in subgroupNodes)
            {
                var subgroupJid = subgroupNode.getattr("jid");
                if (!string.IsNullOrEmpty(subgroupJid))
                {
                    var metadata = await GroupMetaData(subgroupJid);
                    subgroups.Add(metadata);
                }
            }

            return subgroups;
        }

        /// <summary>
        /// Create a subgroup within a community
        /// </summary>
        public async Task<GroupMetadataModel> CommunityCreateSubgroup(string communityJid, string subject, string[] participants)
        {
            var key = GenerateMessageID();
            var result = await GroupQuery("@g.us", "set", new BinaryNode[]
            {
                new BinaryNode
                {
                    tag = "create",
                    attrs = new Dictionary<string, string>
                    {
                        { "subject", subject },
                        { "key", key },
                        { "parent", communityJid },
                        { "default_sub_group", "true" }
                    },
                    content = participants.Select(p => new BinaryNode
                    {
                        tag = "participant",
                        attrs = new Dictionary<string, string> { { "jid", p } }
                    }).ToArray()
                }
            });

            var metadata = ExtractGroupMetaData(result);
            Store.AddGroup(new ContactModel
            {
                ID = metadata.ID,
                Name = subject
            });

            return metadata;
        }

        #endregion

        #region Group Management Enhancements

        /// <summary>
        /// Update group profile picture
        /// </summary>
        public async Task<string> GroupUpdateProfilePicture(string jid, byte[] imageData)
        {
            var mediaUploadData = await GetRawMediaUploadData(imageData, "profile-pic");
            var fileSha256B64 = Convert.ToBase64String(mediaUploadData.FileSha256);

            var uploadResult = await WaUploadToServer(mediaUploadData.FilePath, new MediaUploadOptions
            {
                FileEncSha256B64 = fileSha256B64,
                MediaType = "profile-pic"
            });

            var result = await GroupQuery(jid, "set", new BinaryNode[]
            {
                new BinaryNode
                {
                    tag = "picture",
                    attrs = new Dictionary<string, string>
                    {
                        { "id", uploadResult.Fbid.ToString() },
                        { "type", "image" }
                    }
                }
            });

            return uploadResult.Fbid.ToString();
        }

        /// <summary>
        /// Remove group profile picture
        /// </summary>
        public async Task<bool> GroupRemoveProfilePicture(string jid)
        {
            var result = await GroupQuery(jid, "set", new BinaryNode[]
            {
                new BinaryNode
                {
                    tag = "picture",
                    attrs = new Dictionary<string, string>
                    {
                        { "delete", "true" }
                    }
                }
            });

            return true;
        }

        /// <summary>
        /// Promote participants to admins
        /// </summary>
        public async Task GroupPromoteParticipants(string jid, string[] participants)
        {
            await GroupParticipantsUpdate(jid, participants, ParticipantAction.Promote);
        }

        /// <summary>
        /// Demote participants from admins
        /// </summary>
        public async Task GroupDemoteParticipants(string jid, string[] participants)
        {
            await GroupParticipantsUpdate(jid, participants, ParticipantAction.Demote);
        }

        /// <summary>
        /// Add participants to group
        /// </summary>
        public async Task GroupAddParticipants(string jid, string[] participants)
        {
            await GroupParticipantsUpdate(jid, participants, ParticipantAction.Add);
        }

        /// <summary>
        /// Remove participants from group
        /// </summary>
        public async Task GroupRemoveParticipants(string jid, string[] participants)
        {
            await GroupParticipantsUpdate(jid, participants, ParticipantAction.Remove);
        }

        /// <summary>
        /// Make group announcement only (only admins can send messages)
        /// </summary>
        public async Task GroupSetAnnouncement(string jid, bool announcement = true)
        {
            await GroupSettingUpdate(jid, announcement ? GroupSetting.Announcement : GroupSetting.Not_Announcement);
        }

        /// <summary>
        /// Lock group info (only admins can edit group info)
        /// </summary>
        public async Task GroupSetLocked(string jid, bool locked = true)
        {
            await GroupSettingUpdate(jid, locked ? GroupSetting.Locked : GroupSetting.Unlocked);
        }

        /// <summary>
        /// Set who can add participants to the group
        /// </summary>
        public async Task GroupSetMemberAddMode(string jid, MemberAddMode mode)
        {
            await GroupMemberAddMode(jid, mode);
        }

        /// <summary>
        /// Enable or disable join approval mode
        /// </summary>
        public async Task GroupSetJoinApprovalMode(string jid, MembershipApprovalMode mode)
        {
            await GroupJoinApprovalMode(jid, mode);
        }

        /// <summary>
        /// Approve join requests
        /// </summary>
        public async Task GroupApproveJoinRequests(string jid, string[] participants)
        {
            await GroupRequestParticipantsUpdate(jid, participants, "approve");
        }

        /// <summary>
        /// Reject join requests
        /// </summary>
        public async Task GroupRejectJoinRequests(string jid, string[] participants)
        {
            await GroupRequestParticipantsUpdate(jid, participants, "reject");
        }

        /// <summary>
        /// Get pending join requests
        /// </summary>
        public async Task<GroupMetadataModel> GroupGetJoinRequests(string jid)
        {
            return await GroupRequestParticipantsList(jid);
        }

        /// <summary>
        /// Set ephemeral messages duration
        /// </summary>
        public async Task GroupSetEphemeralDuration(string jid, ulong expirationInSeconds)
        {
            await GroupToggleEphemeral(jid, expirationInSeconds);
        }

        /// <summary>
        /// Disable ephemeral messages
        /// </summary>
        public async Task GroupDisableEphemeral(string jid)
        {
            await GroupToggleEphemeral(jid, 0);
        }

        /// <summary>
        /// Get group invite code
        /// </summary>
        public async Task<string> GroupGetInviteCode(string jid)
        {
            return await GroupInviteCode(jid);
        }

        /// <summary>
        /// Revoke group invite code
        /// </summary>
        public async Task<string> GroupRevokeInvite(string jid)
        {
            return await GroupRevokeInvite(jid);
        }

        /// <summary>
        /// Accept group invite
        /// </summary>
        public async Task<string> GroupAcceptInviteCode(string code)
        {
            return await GroupAcceptInvite(code);
        }

        /// <summary>
        /// Get group invite info without joining
        /// </summary>
        public async Task<GroupMetadataModel> GroupGetInviteInfo(string code)
        {
            return await GroupGetInviteInfo(code);
        }

        /// <summary>
        /// Leave multiple groups
        /// </summary>
        public async Task GroupLeaveMultiple(string[] groupIds)
        {
            foreach (var groupId in groupIds)
            {
                await GroupLeave(groupId);
            }
        }

        /// <summary>
        /// Mute group for a specified duration
        /// </summary>
        public async Task GroupMute(string jid, ulong durationInSeconds)
        {
            var result = await GroupQuery(jid, "set", new BinaryNode[]
            {
                new BinaryNode
                {
                    tag = "mute",
                    attrs = new Dictionary<string, string>
                    {
                        { "duration", durationInSeconds.ToString() }
                    }
                }
            });
        }

        /// <summary>
        /// Unmute group
        /// </summary>
        public async Task GroupUnmute(string jid)
        {
            var result = await GroupQuery(jid, "set", new BinaryNode[]
            {
                new BinaryNode
                {
                    tag = "mute",
                    attrs = new Dictionary<string, string>
                    {
                        { "duration", "0" }
                    }
                }
            });
        }

        /// <summary>
        /// Get all groups the user is participating in
        /// </summary>
        public async Task<List<GroupMetadataModel>> GroupFetchAll()
        {
            await GroupFetchAllParticipating();
            return GetAllGroups().Select(g => new GroupMetadataModel
            {
                ID = g.ID,
                Subject = g.Name
            }).ToList();
        }

        /// <summary>
        /// Search for groups by name or description
        /// </summary>
        public async Task<List<GroupMetadataModel>> GroupSearch(string query)
        {
            var allGroups = await GroupFetchAll();
            return allGroups.Where(g => 
                g.Subject?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
                g.Desc?.Contains(query, StringComparison.OrdinalIgnoreCase) == true
            ).ToList();
        }

        #endregion
    }
}
