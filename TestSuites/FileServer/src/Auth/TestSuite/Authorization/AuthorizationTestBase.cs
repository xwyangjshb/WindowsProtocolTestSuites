﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Protocols.TestSuites.FileSharing.Common.Adapter;
using Microsoft.Protocols.TestSuites.FileSharing.Common.TestSuite;
using Microsoft.Protocols.TestTools;
using Microsoft.Protocols.TestTools.StackSdk;
using Microsoft.Protocols.TestTools.StackSdk.Dtyp;
using Microsoft.Protocols.TestTools.StackSdk.FileAccessService.Smb2;
using Microsoft.Protocols.TestTools.StackSdk.Security.Sspi;
using Microsoft.Protocols.TestTools.StackSdk.Security.SspiLib;
using Microsoft.Protocols.TestTools.StackSdk.Srvs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Novell.Directory.Ldap;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.DirectoryServices;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Microsoft.Protocols.TestSuites.FileSharing.Auth.TestSuite
{
    [TestClass]
    public class AuthorizationTestBase : CommonTestBase
    {
        protected const string azUser01Name = "AzUser01";
        protected const string azGroup01Name = "AzGroup01";
        public AuthTestConfig TestConfig
        {
            get
            {
                return testConfig as AuthTestConfig;
            }
        }
        protected override void TestInitialize()
        {
            base.TestInitialize();
            testConfig = new AuthTestConfig(BaseTestSite);

            // valid domain name matching, for example: contoso.com, local.contoso.com
            if (!Regex.IsMatch(TestConfig.DomainName, @"^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9][a-z0-9-]{0,61}[a-z0-9]$", RegexOptions.IgnoreCase))
            {
                BaseTestSite.Assert.Inconclusive("Authentication test cases are not applicable in non-domain environment");
            }
        }

        protected bool AccessShare(AccountCredential user, string sharePath)
        {
            bool accessSucceed = true;
            Smb2FunctionalClient client = new Smb2FunctionalClient(TestConfig.Timeout, TestConfig, BaseTestSite);
            client.ConnectToServer(TestConfig.UnderlyingTransport, TestConfig.SutComputerName, TestConfig.SutIPAddress);
            try
            {
                BaseTestSite.Log.Add(LogEntryKind.Debug, "Client sends NEGOTIATE message.");
                client.Negotiate(TestConfig.RequestDialects, TestConfig.IsSMB1NegotiateEnabled);
                BaseTestSite.Log.Add(LogEntryKind.Debug, "Client sends SESSION_SETUP message using account: {0}@{1}.", user.AccountName, user.DomainName);
                client.SessionSetup(TestConfig.DefaultSecurityPackage, TestConfig.SutComputerName, user, false);

                uint treeId;
                BaseTestSite.Log.Add(LogEntryKind.Debug, "Client sends TREE_CONNECT message to access share: {0}.", sharePath);
                client.TreeConnect(sharePath, out treeId, checker: (header, response) =>
                {
                    if (header.Status == Smb2Status.STATUS_SUCCESS)
                    {
                        BaseTestSite.Log.Add(LogEntryKind.Debug, "Access succeeded in TREE_CONNECT phase.");
                        accessSucceed = true;
                    }
                    else if (header.Status == Smb2Status.STATUS_ACCESS_DENIED)
                    {
                        BaseTestSite.Log.Add(LogEntryKind.Debug, "Access denied in TREE_CONNECT phase.");
                        accessSucceed = false;
                    }
                    else
                    {
                        BaseTestSite.Assert.Fail("Unexpected error code in TREE_CONNECT response: {0}", Smb2Status.GetStatusCode(header.Status));
                    }
                });

                if (!accessSucceed)
                {
                    client.LogOff();
                    return false;
                }

                FILEID fileId;
                Smb2CreateContextResponse[] createContexResponse;

                BaseTestSite.Log.Add(LogEntryKind.Debug, "Client sends CREATE request.");
                uint status = client.Create(
                    treeId,
                    null,
                    CreateOptions_Values.FILE_DIRECTORY_FILE,
                    out fileId,
                    out createContexResponse,
                    accessMask: AccessMask.FILE_READ_DATA | AccessMask.FILE_READ_ATTRIBUTES,
                    createDisposition: CreateDisposition_Values.FILE_OPEN,
                    checker: (header, response) =>
                    {
                        if (header.Status == Smb2Status.STATUS_SUCCESS)
                        {
                            BaseTestSite.Log.Add(LogEntryKind.Debug, "Access succeeded in CREATE phase.");
                            accessSucceed = true;
                        }
                        else if (header.Status == Smb2Status.STATUS_ACCESS_DENIED)
                        {
                            BaseTestSite.Log.Add(LogEntryKind.Debug, "Access denied in CREATE phase.");
                            accessSucceed = false;
                        }
                        else
                        {
                            BaseTestSite.Assert.Fail("Unexpected error code in CREATE response: {0}", Smb2Status.GetStatusCode(header.Status));
                        }
                    });

                if (status == Smb2Status.STATUS_SUCCESS)
                {
                    BaseTestSite.Log.Add(LogEntryKind.Debug, "Tear down the client by sending the following requests: CLOSE; TREE_DISCONNECT; LOG_OFF.");
                    client.Close(treeId, fileId);
                }
                client.TreeDisconnect(treeId);
                client.LogOff();
            }
            finally
            {
                client.Disconnect();
            }
            return accessSucceed;
        }

        protected bool TryReadFile(Smb2FunctionalClient client, AccountCredential user, string sharePath, string fileName)
        {
            bool accessSucceed = true;

            BaseTestSite.Log.Add(LogEntryKind.Debug, "Client sends NEGOTIATE message.");
            client.Negotiate(TestConfig.RequestDialects, TestConfig.IsSMB1NegotiateEnabled);
            BaseTestSite.Log.Add(LogEntryKind.Debug, "Client sends SESSION_SETUP message using account: {0}@{1}.", user.AccountName, user.DomainName);
            client.SessionSetup(TestConfig.DefaultSecurityPackage, TestConfig.SutComputerName, user, false);

            uint treeId;
            BaseTestSite.Log.Add(LogEntryKind.Debug, "Client sends TREE_CONNECT message to access share: {0}.", sharePath);
            client.TreeConnect(sharePath, out treeId);

            FILEID fileId;
            Smb2CreateContextResponse[] createContexResponse;

            BaseTestSite.Log.Add(LogEntryKind.Debug, "Client sends CREATE request.");
            uint status = client.Create(
                treeId,
                fileName,
                CreateOptions_Values.FILE_NON_DIRECTORY_FILE,
                out fileId,
                out createContexResponse,
                accessMask: AccessMask.FILE_READ_DATA | AccessMask.FILE_READ_ATTRIBUTES,
                createDisposition: CreateDisposition_Values.FILE_OPEN,
                checker: (header, response) =>
                {
                    if (header.Status == Smb2Status.STATUS_SUCCESS)
                    {
                        BaseTestSite.Log.Add(LogEntryKind.Debug, "Successfully opened the file with Access Mask: FILE_READ_DATA | FILE_READ_ATTRIBUTES.");
                        accessSucceed = true;
                    }
                    else if (header.Status == Smb2Status.STATUS_ACCESS_DENIED)
                    {
                        BaseTestSite.Log.Add(LogEntryKind.Debug, "Fail to open the file with Access Mask: FILE_READ_DATA | FILE_READ_ATTRIBUTES.");
                        accessSucceed = false;
                    }
                    else
                    {
                        BaseTestSite.Assert.Fail("Unexpected error code in CREATE response: {0}", Smb2Status.GetStatusCode(header.Status));
                    }
                });

            if (status == Smb2Status.STATUS_SUCCESS)
            {
                BaseTestSite.Log.Add(LogEntryKind.Debug, "Tear down the client by sending the following requests: CLOSE; TREE_DISCONNECT; LOG_OFF.");
                client.Close(treeId, fileId);
            }
            client.TreeDisconnect(treeId);
            client.LogOff();
            return accessSucceed;
        }

        protected bool ShareExists(AccountCredential user, string sharePath)
        {
            BaseTestSite.Log.Add(LogEntryKind.Debug, "Test whether the specific share exists: {0}", sharePath);
            Smb2FunctionalClient clientAdmin;
            clientAdmin = new Smb2FunctionalClient(TestConfig.Timeout, TestConfig, BaseTestSite);
            clientAdmin.ConnectToServer(TestConfig.UnderlyingTransport, TestConfig.SutComputerName, TestConfig.SutIPAddress);
            uint treeId;
            bool connected = ConnectToShare(clientAdmin, user, sharePath, out treeId);
            if (connected)
            {
                BaseTestSite.Log.Add(LogEntryKind.Debug, "Share exists: {0}", sharePath);
                clientAdmin.TreeDisconnect(treeId);
                clientAdmin.LogOff();
            }
            else
            {
                BaseTestSite.Log.Add(LogEntryKind.Debug, "Share does not exist: {0}", sharePath);
            }
            clientAdmin.Disconnect();
            return connected;
        }

        protected bool ConnectToShare(Smb2FunctionalClient client, AccountCredential user, string sharePath, out uint treeId)
        {
            BaseTestSite.Log.Add(LogEntryKind.Debug, "Client sends NEGOTIATE message.");
            client.Negotiate(TestConfig.RequestDialects, TestConfig.IsSMB1NegotiateEnabled);
            AccountCredential accountCredential = user;
            BaseTestSite.Log.Add(LogEntryKind.Debug, "Client sends SESSION_SETUP message using account: {0}@{1}.", accountCredential.AccountName, accountCredential.DomainName);
            client.SessionSetup(TestConfig.DefaultSecurityPackage, TestConfig.SutComputerName, accountCredential, false);

            BaseTestSite.Log.Add(LogEntryKind.Debug, "Client sends TREE_CONNECT message to access share: {0}.", sharePath);
            uint status = client.TreeConnect(sharePath, out treeId, checker: (header, response) => { });
            if (status == Smb2Status.STATUS_SUCCESS)
            {
                return true;
            }
            if (status == Smb2Status.STATUS_BAD_NETWORK_NAME)
            {
                return false;
            }
            throw new Exception(string.Format("Share detection failed with unexpected error: {0}", Smb2Status.GetStatusCode(status)));
        }

        protected void CreateNewFile(string sharePath, string fileName)
        {
            Smb2FunctionalClient clientAdmin;
            clientAdmin = new Smb2FunctionalClient(TestConfig.Timeout, TestConfig, BaseTestSite);
            clientAdmin.ConnectToServer(TestConfig.UnderlyingTransport, TestConfig.SutComputerName, TestConfig.SutIPAddress);
            uint treeId;
            ConnectToShare(clientAdmin, TestConfig.AccountCredential, sharePath, out treeId);

            FILEID fileId;
            Smb2CreateContextResponse[] createContextResponses;
            clientAdmin.Create(
                treeId,
                fileName,
                CreateOptions_Values.FILE_NON_DIRECTORY_FILE,
                out fileId,
                out createContextResponses,
                accessMask: AccessMask.FILE_READ_DATA | AccessMask.FILE_WRITE_DATA |
                            AccessMask.FILE_APPEND_DATA | AccessMask.FILE_READ_EA |
                            AccessMask.FILE_WRITE_EA | AccessMask.READ_CONTROL |
                            AccessMask.WRITE_DAC | AccessMask.FILE_READ_ATTRIBUTES |
                            AccessMask.FILE_WRITE_ATTRIBUTES | AccessMask.SYNCHRONIZE,
                shareAccess: ShareAccess_Values.NONE,
                createDisposition: CreateDisposition_Values.FILE_CREATE);
            clientAdmin.Close(treeId, fileId);
            clientAdmin.TreeDisconnect(treeId);
            clientAdmin.Disconnect();
        }

        protected void DeleteExistingFile(string sharePath, string fileName)
        {
            Smb2FunctionalClient clientAdmin;
            clientAdmin = new Smb2FunctionalClient(TestConfig.Timeout, TestConfig, BaseTestSite);
            clientAdmin.ConnectToServer(TestConfig.UnderlyingTransport, TestConfig.SutComputerName, TestConfig.SutIPAddress);
            uint treeId;
            ConnectToShare(clientAdmin, TestConfig.AccountCredential, sharePath, out treeId);

            FILEID fileId;
            Smb2CreateContextResponse[] createContextResponses;
            clientAdmin.Create(
                treeId,
                fileName,
                CreateOptions_Values.FILE_NON_DIRECTORY_FILE | CreateOptions_Values.FILE_DELETE_ON_CLOSE,
                out fileId,
                out createContextResponses,
                accessMask: AccessMask.FILE_READ_ATTRIBUTES | AccessMask.DELETE,
                shareAccess: ShareAccess_Values.FILE_SHARE_DELETE,
                createDisposition: CreateDisposition_Values.FILE_OPEN);
            clientAdmin.Close(treeId, fileId);
            clientAdmin.TreeDisconnect(treeId);
            clientAdmin.Disconnect();
        }

        protected void SetSecurityDescriptor(string sharePath, string fileName, _SECURITY_DESCRIPTOR sd, SET_INFO_Request_AdditionalInformation_Values securityAttributesToApply)
        {
            Smb2FunctionalClient clientAdmin;
            clientAdmin = new Smb2FunctionalClient(TestConfig.Timeout, TestConfig, BaseTestSite);
            clientAdmin.ConnectToServer(TestConfig.UnderlyingTransport, TestConfig.SutComputerName, TestConfig.SutIPAddress);
            uint treeId;
            ConnectToShare(clientAdmin, TestConfig.AccountCredential, sharePath, out treeId);

            FILEID fileId;
            Smb2CreateContextResponse[] createContextResponses;
            clientAdmin.Create(treeId,
                fileName,
                fileName == null ? CreateOptions_Values.FILE_DIRECTORY_FILE : CreateOptions_Values.FILE_NON_DIRECTORY_FILE,
                out fileId,
                out createContextResponses,
                accessMask: AccessMask.READ_CONTROL | AccessMask.WRITE_DAC | AccessMask.FILE_READ_ATTRIBUTES | AccessMask.WRITE_OWNER | AccessMask.ACCESS_SYSTEM_SECURITY,
                shareAccess: ShareAccess_Values.FILE_SHARE_DELETE | ShareAccess_Values.FILE_SHARE_READ | ShareAccess_Values.FILE_SHARE_WRITE,
                createDisposition: CreateDisposition_Values.FILE_OPEN);
            clientAdmin.SetSecurityDescriptor(treeId, fileId, securityAttributesToApply, sd);
            clientAdmin.Close(treeId, fileId);
            clientAdmin.TreeDisconnect(treeId);
            clientAdmin.Disconnect();
        }

        protected _SECURITY_DESCRIPTOR QuerySecurityDescriptor(string sharePath, string fileName, AdditionalInformation_Values securityAttributesToQuery)
        {
            Smb2FunctionalClient clientAdmin;
            clientAdmin = new Smb2FunctionalClient(TestConfig.Timeout, TestConfig, BaseTestSite);
            clientAdmin.ConnectToServer(TestConfig.UnderlyingTransport, TestConfig.SutComputerName, TestConfig.SutIPAddress);
            uint treeId;
            ConnectToShare(clientAdmin, TestConfig.AccountCredential, sharePath, out treeId);

            FILEID fileId;
            Smb2CreateContextResponse[] createContextResponses;
            clientAdmin.Create(treeId,
                fileName,
                fileName == null ? CreateOptions_Values.FILE_DIRECTORY_FILE : CreateOptions_Values.FILE_NON_DIRECTORY_FILE,
                out fileId,
                out createContextResponses,
                accessMask: AccessMask.READ_CONTROL | AccessMask.WRITE_DAC | AccessMask.FILE_READ_ATTRIBUTES,
                shareAccess: ShareAccess_Values.FILE_SHARE_DELETE | ShareAccess_Values.FILE_SHARE_READ | ShareAccess_Values.FILE_SHARE_WRITE,
                createDisposition: CreateDisposition_Values.FILE_OPEN);
            _SECURITY_DESCRIPTOR sd;
            clientAdmin.QuerySecurityDescriptor(treeId, fileId, securityAttributesToQuery, out sd);
            clientAdmin.Close(treeId, fileId);
            clientAdmin.TreeDisconnect(treeId);
            clientAdmin.LogOff();
            clientAdmin.Disconnect();
            return sd;
        }

        protected void SetShareInfo(string sharePath, SHARE_INFO_502_I shareInfo)
        {
            using (SrvsClient srvsClient = new SrvsClient(TestConfig.Timeout))
            {
                srvsClient.Bind(TestConfig.SutComputerName, TestConfig.AccountCredential, null);
                SHARE_INFO info = new SHARE_INFO { ShareInfo502 = shareInfo };
                uint? parmErr = 0;
                uint retVal = srvsClient.NetrShareSetInfo(@"\\" + TestConfig.SutComputerName, sharePath, SHARE_ENUM_STRUCT_LEVEL.Level502, info, ref parmErr);
                if (retVal != 0)
                {
                    BaseTestSite.Assert.Fail("Fail to get share info through MS-SRVS.");
                }
                srvsClient.UnBind();
            }
        }

        protected SHARE_INFO_502_I GetShareInfo(string sharePath)
        {
            using (SrvsClient srvsClient = new SrvsClient(TestConfig.Timeout))
            {
                srvsClient.Bind(TestConfig.SutComputerName, TestConfig.AccountCredential, null);

                SHARE_INFO? shareInfo;
                uint retVal = srvsClient.NetrShareGetInfo(@"\\" + TestConfig.SutComputerName, sharePath, SHARE_ENUM_STRUCT_LEVEL.Level502, out shareInfo);
                if (retVal != 0 || shareInfo == null || shareInfo.Value.ShareInfo502 == null)
                {
                    BaseTestSite.Assert.Fail("Fail to get share info through MS-SRVS.");
                }
                srvsClient.UnBind();
                return shareInfo.Value.ShareInfo502.Value;
            }
        }

        protected Dictionary<string, CentralAccessPolicy> QueryCaps(string domainName, string userName, string password)
        {
            Dictionary<string, CentralAccessRule> rules = new Dictionary<string, CentralAccessRule>();
            Dictionary<string, CentralAccessPolicy> policies = new Dictionary<string, CentralAccessPolicy>();

            string[] domainNameTokens = domainName.Split('.');
            string admin = $"{domainNameTokens[0].ToUpper()}\\{userName}";
            StringBuilder bindString = new StringBuilder("CN=Claims Configuration,CN=Services,CN=Configuration");
            foreach (string domainNameToken in domainNameTokens)
            {
                bindString.Append(",DC=");
                bindString.Append(domainNameToken);
            }
            string searchBase = bindString.ToString();
            using (LdapConnection conn = new LdapConnection())
            {
                conn.Connect(domainName, 389);
                conn.Bind(admin, password);

                var results = conn.Search(searchBase, LdapConnection.ScopeSub, "(objectClass=msAuthz-CentralAccessRule)", new string[] { "cn", "distinguishedName", "msAuthz-EffectiveSecurityPolicy", "msAuthz-ResourceCondition" }, false);
                var entryList = results.GetAllLdapEntries();
                foreach (KeyValuePair<string, IList<LdapAttribute>> kvp in entryList)
                {

                    string dn = kvp.Value.GetStringValueFromAttributes("distinguishedName");
                    string carName = kvp.Value.GetStringValueFromAttributes("cn");
                    string sddl = kvp.Value.GetStringValueFromAttributes("msAuthz-EffectiveSecurityPolicy");
                    string resourceCondition = kvp.Value.GetStringValueFromAttributes("msAuthz-ResourceCondition");

                    CentralAccessRule rule = new CentralAccessRule { Name = carName, Sddl = sddl, ResourceCondition = resourceCondition };
                    rules.Add(dn, rule);

                }

                results = conn.Search(searchBase, LdapConnection.ScopeSub, "(objectClass=msAuthz-CentralAccessPolicy)", new string[] { "cn", "msAuthz-CentralAccessPolicyID", "msAuthz-MemberRulesInCentralAccessPolicy" }, false);
                var policyEntryList = results.GetAllLdapEntries();
                foreach (KeyValuePair<string, IList<LdapAttribute>> kvp in policyEntryList)
                {
                    CentralAccessPolicy policy = new CentralAccessPolicy();
                    string capName = kvp.Value.GetStringValueFromAttributes("cn");
                    policy.Name = capName;
                    byte[] sidInBinary = (byte[])kvp.Value.GetBytesValueFromAttributes("msAuthz-CentralAccessPolicyID")[0];
                    _SID capId = TypeMarshal.ToStruct<_SID>(sidInBinary);
                    policy.Id = capId;
                    IList<string> rulesPath = kvp.Value.GetStringListValueFromAttributes("msAuthz-MemberRulesInCentralAccessPolicy");
                    foreach (string ruleDN in rulesPath)
                    {
                        policy.MemberRules.Add(rules[ruleDN]);
                    }

                    policies.Add(capName, policy);
                }

                conn.Disconnect();
            }

            return policies;
        }

        protected Dictionary<string, User> QueryUserInfo(string domainName, string userName, string password)
        {
            Dictionary<string, User> users = new Dictionary<string, User>();
            string[] domainNameTokens = domainName.Split('.');
            string admin = $"{domainNameTokens[0].ToUpper()}\\{userName}";
            StringBuilder bindString = new StringBuilder("CN=Users");
            foreach (string domainNameToken in domainNameTokens)
            {
                bindString.Append(",DC=");
                bindString.Append(domainNameToken);
            }

            string searchBase = bindString.ToString();
            using (LdapConnection conn = new LdapConnection())
            {
                conn.Connect(domainName, 389);
                conn.Bind(admin, password);

                var results = conn.Search(searchBase, LdapConnection.ScopeSub, "(objectClass=user)", new string[] { "cn", "countryCode", "department", "objectSid" }, false);
                var entryList = results.GetAllLdapEntries();
                foreach (KeyValuePair<string, IList<LdapAttribute>> kvp in entryList)
                {
                    User user = new User();
                    string name = kvp.Value.GetStringValueFromAttributes("cn");
                    user.Name = name;
                    string countryCodeStr = kvp.Value.GetStringValueFromAttributes("countryCode");
                    if (string.IsNullOrEmpty(countryCodeStr))
                    {
                        int countryCode = 0;
                        if (int.TryParse(countryCodeStr, out countryCode))
                        {
                            user.CountryCode = countryCode;
                        }
                    }
                    user.Department = kvp.Value.GetStringValueFromAttributes("department");

                    byte[] sidInBinary = (byte[])kvp.Value.GetBytesValueFromAttributes("objectSid")[0];
                    _SID userSid = TypeMarshal.ToStruct<_SID>(sidInBinary);
                    user.Sid = userSid;
                    users.Add(name, user);
                }
            }

            return users;
        }

        protected void SetCap(string sharePath, _SID? capId)
        {
            _ACL sacl;
            if (capId != null)
            {
                _SYSTEM_SCOPED_POLICY_ID_ACE ace = DtypUtility.CreateSystemScopedPolicyIdAce(capId.Value);
                sacl = DtypUtility.CreateAcl(false, ace);
            }
            else
            {
                sacl = DtypUtility.CreateAcl(false);
            }

            _SECURITY_DESCRIPTOR sd = DtypUtility.CreateSecurityDescriptor(
                SECURITY_DESCRIPTOR_Control.SACLAutoInherited | SECURITY_DESCRIPTOR_Control.SACLInheritanceRequired |
                SECURITY_DESCRIPTOR_Control.SACLPresent | SECURITY_DESCRIPTOR_Control.SelfRelative,
                null,
                null,
                sacl,
                null);
            if (sharePath != null)
            {
                SetSecurityDescriptor(sharePath, null, sd, SET_INFO_Request_AdditionalInformation_Values.SCOPE_SECURITY_INFORMATION);
            }
        }

        protected class CentralAccessPolicy
        {
            public string Name;
            public _SID Id;
            public List<CentralAccessRule> MemberRules;

            public CentralAccessPolicy()
            {
                MemberRules = new List<CentralAccessRule>();
            }

            public override string ToString()
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendFormat("Central Access Policy Name: {0}", Name);
                sb.AppendFormat(", Id: {0}", DtypUtility.ToSddlString(Id));
                for (int i = 0; i < MemberRules.Count; i++)
                {
                    sb.AppendFormat(", Central Access Rule {0}: [{1}]", i, MemberRules[i]);
                }
                return sb.ToString();
            }
        }

        protected class CentralAccessRule
        {
            public string Name;
            public string Sddl;
            public string ResourceCondition;
            public override string ToString()
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendFormat("Name: {0}", Name);
                sb.AppendFormat(", Sddl: {0}", Sddl);
                sb.AppendFormat(", Resouce Condition: {0}", ResourceCondition);
                return sb.ToString();
            }
        }

        protected class User
        {
            public _SID Sid;
            public string Name;
            public int? CountryCode;
            public string Department;
            public override string ToString()
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendFormat("Name: {0}", Name);
                sb.AppendFormat(", Country Code: {0}", CountryCode);
                sb.AppendFormat(", Department: {0}", Department);
                return sb.ToString();
            }
        }
    }
}
