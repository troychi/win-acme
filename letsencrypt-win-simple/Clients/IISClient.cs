﻿using Microsoft.Web.Administration;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography.X509Certificates;

namespace LetsEncrypt.ACME.Simple.Clients
{
    public class IISClient : Plugin
    {
        private Version _iisVersion = GetIisVersion();
        private IdnMapping _idnMapping = new IdnMapping();
        public const string PluginName = "IIS";

        public override string Name => PluginName;

        public List<string> GetHosts(Site site)
        {
            return site.Bindings.Select(x => x.Host.ToLower()).
                            Where(x => !string.IsNullOrWhiteSpace(x)).
                            Select(x => _idnMapping.GetAscii(x)).
                            Distinct().
                            ToList();
        }

        public List<Target> GetBindings(Options options, bool logInvalidSites)
        {
            Program.Log.Debug("Scanning IIS site bindings for hosts");
            if (_iisVersion.Major == 0)
            {
                Program.Log.Warning("IIS version not found in windows registry. Skipping scan.");
            }
            else
            {
                using (var iisManager = new ServerManager())
                {
                    // Get all bindings matched together with their respective sites
                    var siteBindings = iisManager.Sites.
                        //Where(s => s.State == ObjectState.Started).
                        SelectMany(site => site.Bindings, (site, binding) => new { site, binding }).
                        Where(sb => !string.IsNullOrWhiteSpace(sb.binding.Host));

                    // Option: hide http bindings when there are already https equivalents
                    if (options.HideHttps)
                    {
                        siteBindings = siteBindings.Where(sb => 
                            sb.binding.Protocol == "http" &&
                            !sb.site.Bindings.Any(other => other.Protocol == "https" &&
                            string.Equals(sb.binding.Host, other.Host, StringComparison.InvariantCultureIgnoreCase)));
                    }

                    var targets = siteBindings.
                        Select(sb => new {
                            idn = _idnMapping.GetAscii(sb.binding.Host.ToLower()),
                            sb.site,
                            sb.binding
                        }).
                        Select(sbi => new Target
                        {
                            SiteId = sbi.site.Id,
                            Host = sbi.idn,
                            HostIsDns = true,
                            IIS = true,
                            WebRootPath = sbi.site.Applications["/"].VirtualDirectories["/"].PhysicalPath,
                            PluginName = PluginName
                        }).
                        DistinctBy(t => t.Host).
                        OrderBy(t => t.SiteId).
                        ToList();
     
                    if (targets.Count() == 0)
                    {
                        Program.Log.Warning("No IIS bindings with host names were found. A host name is required to verify domain ownership.");
                    }
                    return targets;
                }
            }
            return new List<Target>();
        }

        public List<Target> GetSites(Options options, bool logInvalidSites)
        {
            var result = new List<Target>();
            Program.Log.Debug("Scanning IIS sites");
            if (_iisVersion.Major == 0)
            {
                Program.Log.Warning("IIS version not found in windows registry. Skipping scan.");
            }
            else
            {
                using (var iisManager = new ServerManager())
                {
                    // Get all bindings matched together with their respective sites
                    var sites = iisManager.Sites.
                        AsEnumerable();
                        // Where(s => s.State == ObjectState.Started);

                    // Option: hide http bindings when there are already https equivalents
                    if (options.HideHttps)
                    {
                        sites = sites.Where(site => site.Bindings.
                            Where(binding => binding.Protocol == "http").
                            Any(binding => !site.Bindings.Any(other => other.Protocol == "https" &&
                            string.Equals(other.Host, binding.Host, StringComparison.InvariantCultureIgnoreCase))));
                    }

                    var targets = sites.
                        Select(site => new Target
                        {
                            SiteId = site.Id,
                            Host = site.Name,
                            HostIsDns = false,
                            WebRootPath = site.Applications["/"].VirtualDirectories["/"].PhysicalPath,
                            PluginName = PluginName,
                            IIS = true,
                            AlternativeNames = GetHosts(site)
                        }).
                        Where(target =>
                        {
                            if (target.AlternativeNames.Count > Settings.maxNames)
                            {
                                if (logInvalidSites)
                                {
                                    Program.Log.Information("{site} has too many hosts for a single certificate. Let's Encrypt has a maximum of {maxNames}.", target.Host, Settings.maxNames);
                                }
                                return false;
                            }
                            else if (target.AlternativeNames.Count == 0)
                            {
                                if (logInvalidSites)
                                {
                                    Program.Log.Information("No valid hosts found for {site}.", target.Host);
                                }
                                return false;
                            }
                            return true;
                        }).
                        OrderBy(target => target.SiteId).
                        ToList();

                    if (targets.Count() == 0 && logInvalidSites)
                    {
                        Program.Log.Warning("No applicable IIS sites were found.");
                    }
                    return targets;
                }
            }
            return new List<Target>();
        }

        internal void UnlockSection(string path)
        {
            if (_iisVersion.Major > 0)
            {
                // Unlock handler section
                using (var sm = new ServerManager())
                {
                    var config = sm.GetApplicationHostConfiguration();
                    var section = config.GetSection(path);
                    if (section.OverrideModeEffective == OverrideMode.Deny)
                    {
                        section.OverrideMode = OverrideMode.Allow;
                        sm.CommitChanges();
                        Program.Log.Warning("Unlocked section {section}", path);
                    }
                }
            }
        }

        /// <summary>
        /// Install for regular bindings
        /// </summary>
        /// <param name="target"></param>
        /// <param name="pfxFilename"></param>
        /// <param name="store"></param>
        /// <param name="certificate"></param>
        public override void Install(Target target, string pfxFilename, X509Store store, X509Certificate2 certificate)
        {
            using (var iisManager = new ServerManager())
            {
                var site = GetSite(target, iisManager);
                var hosts = target.GetHosts(true);
                foreach (var host in hosts)
                {
                    var existingBindings = (from b in site.Bindings
                                            where string.Equals(b.Host, host, StringComparison.InvariantCultureIgnoreCase)
                                            where b.Protocol == "https"
                                            select b).ToList();
                    if (existingBindings.Any())
                    {
                        foreach (var existingBinding in existingBindings)
                        {
                            Program.Log.Information(true, "Updating existing https binding {host}:{port}", host, existingBinding.EndPoint.Port);
    
                            // Replace instead of change binding because of #371
                            Binding replacement = site.Bindings.CreateElement("binding");
                            replacement.Protocol = existingBinding.Protocol;
                            replacement.BindingInformation = existingBinding.BindingInformation;
                            replacement.CertificateStoreName = store.Name;
                            replacement.CertificateHash = certificate.GetCertHash();
                            foreach (ConfigurationAttribute attr in existingBinding.Attributes)
                            {
                                replacement.SetAttributeValue(attr.Name, attr.Value);
                            }
                            site.Bindings.Remove(existingBinding);
                            site.Bindings.Add(replacement);
                        }
                    }
                    else
                    {
                        Program.Log.Information(true, "Adding new https binding for {host}", host);
                        var existingHTTPBinding = 
                            (from b in site.Bindings
                             where string.Equals(b.Host, host, StringComparison.CurrentCultureIgnoreCase)
                             where b.Protocol == "http"
                             select b).FirstOrDefault();
                        if (existingHTTPBinding != null)
                        {
                            string IP = GetIP(existingHTTPBinding.EndPoint.ToString(), host);
                            Binding iisBinding = site.Bindings.CreateElement("binding");
                            iisBinding.Protocol = "https";
                            iisBinding.BindingInformation = $"{IP}:{Program.Options.SSLPort}:{host}";
                            iisBinding.CertificateStoreName = store.Name;
                            iisBinding.CertificateHash = certificate.GetCertHash();
                            if (_iisVersion.Major >= 8)
                            {
                                iisBinding.SetAttributeValue("sslFlags", 1); // Enable SNI support
                            }
                            site.Bindings.Add(iisBinding);
                        }
                        else
                        {
                            Program.Log.Warning("No HTTP binding for {host} on {name}", host, site.Name);
                        }
                    }
                }
                Program.Log.Information("Committing binding changes to IIS");
                iisManager.CommitChanges();
                Program.Log.Information("IIS will serve the new certificate after the Application Pool IdleTimeout has been reached.");
            }
        }

        /// <summary>
        /// Install for Central SSL bindings
        /// </summary>
        /// <param name="target"></param>
        public override void Install(Target target)
        {
            if (!(_iisVersion.Major >= 8))
            {
                var errorMessage = "You aren't using IIS 8 or greater, so Centralized SSL is not supported";
                Program.Log.Error(errorMessage);
                //Not using IIS 8+ so can't set centralized certificates
                throw new InvalidOperationException(errorMessage);
            }

            try
            {
                using (var iisManager = new ServerManager())
                {
                    var site = GetSite(target, iisManager);
                    var hosts = target.GetHosts(true);
                    foreach (var host in hosts)
                    {
                        var existingBindings = 
                            (from b in site.Bindings
                             where string.Equals(b.Host, host, StringComparison.CurrentCultureIgnoreCase)
                             where b.Protocol == "https"
                             select b).ToList();
                        if (existingBindings.Any())
                        {
                            foreach (var existingBinding in existingBindings)
                            {
                                if (existingBinding.GetAttributeValue("sslFlags").ToString() != "3")
                                {
                                    Program.Log.Information(true, "Converting existing https binding {host}:{port} to Central SSL", host, existingBinding.EndPoint.Port);
                                    //IIS 8+ and not using centralized SSL with SNI
                                    existingBinding.CertificateStoreName = null;
                                    existingBinding.CertificateHash = null;
                                    existingBinding.SetAttributeValue("sslFlags", 3);
                                }
                                else
                                {
                                    Program.Log.Verbose("You specified Central SSL, have an existing binding using Central SSL with SNI, so there is nothing to update for this binding");
                                }
                            }
                        }
                        else
                        {
                            Program.Log.Information(true, "Adding Central SSL https binding");
                            var existingHTTPBinding = 
                                (from b in site.Bindings
                                 where string.Equals(b.Host, host, StringComparison.InvariantCultureIgnoreCase)
                                 where b.Protocol == "http"
                                 select b).FirstOrDefault();
                            if (existingHTTPBinding != null)
                            //This had been a fix for the multiple site San cert, now it's a precaution against erroring out
                            {
                                string IP = GetIP(existingHTTPBinding.EndPoint.ToString(), host);

                                var iisBinding = site.Bindings.Add($"{IP}:{Program.Options.SSLPort}:{host}", "https");

                                iisBinding.SetAttributeValue("sslFlags", 3);
                                // Enable Centralized Certificate Store with SNI
                            }
                            else
                            {
                                Program.Log.Warning("No HTTP binding for {host} on {name}", host, site.Name);
                            }
                        }
                    }
                    Program.Log.Information("Committing binding changes to IIS");
                    iisManager.CommitChanges();
                }
            }
            catch (Exception ex)
            {
                Program.Log.Error("Error setting binding {@ex}", ex);
                throw new InvalidProgramException(ex.Message);
            }
        }

        private static Version GetIisVersion()
        {
            using (RegistryKey componentsKey = Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\InetStp", false))
            {
                if (componentsKey != null)
                {
                    int majorVersion = (int)componentsKey.GetValue("MajorVersion", -1);
                    int minorVersion = (int)componentsKey.GetValue("MinorVersion", -1);

                    if (majorVersion != -1 && minorVersion != -1)
                    {
                        return new Version(majorVersion, minorVersion);
                    }
                }

                return new Version(0, 0);
            }
        }

        public override void Renew(Target target)
        {
            Auto(target);
        }

        protected Site GetSite(Target target, ServerManager iisManager)
        {
            foreach (var site in iisManager.Sites)
            {
                if (site.Id == target.SiteId)
                    return site;
            }
            throw new System.Exception($"Unable to find IIS site ID #{target.SiteId} for binding {this}");
        }

        private string GetIP(string HTTPEndpoint, string host)
        {
            string IP = "*";
            string HTTPIP = HTTPEndpoint.Remove(HTTPEndpoint.IndexOf(':'),
                (HTTPEndpoint.Length - HTTPEndpoint.IndexOf(':')));

            if (_iisVersion.Major >= 8 && HTTPIP != "0.0.0.0")
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"\r\nWarning creating HTTPS Binding for {host}.");
                Console.ResetColor();
                Console.WriteLine(
                    "The HTTP binding is IP specific; the app can create it. However, if you have other HTTPS sites they will all get an invalid certificate error until you manually edit one of their HTTPS bindings.");
                Console.WriteLine("\r\nYou need to edit the binding, turn off SNI, click OK, edit it again, enable SNI and click OK. That should fix the error.");
                Console.WriteLine("\r\nOtherwise, manually create the HTTPS binding and rerun the application.");
                Console.WriteLine("\r\nYou can see https://github.com/Lone-Coder/letsencrypt-win-simple/wiki/HTTPS-Binding-With-Specific-IP for more information.");
                Console.WriteLine(
                    "\r\nPress Y to acknowledge this and continue. Press any other key to stop installing the certificate");
                var response = Console.ReadKey(true);
                if (response.Key == ConsoleKey.Y)
                {
                    IP = HTTPIP;
                }
                else
                {
                    throw new Exception(
                        "HTTPS Binding not created due to HTTP binding having specific IP; Manually create the HTTPS binding and retry");
                }
            }
            else if (HTTPIP != "0.0.0.0")
            {
                IP = HTTPIP;
            }
            return IP;
        }

        internal Target UpdateWebRoot(Target saved, Target match)
        {
            // Update web root path
            if (!string.Equals(saved.WebRootPath, match.WebRootPath, StringComparison.InvariantCultureIgnoreCase))
            {
                Program.Log.Warning("- Change WebRootPath from {old} to {new}", saved.WebRootPath, match.WebRootPath);
                saved.WebRootPath = match.WebRootPath;
            }
            return saved;
        }

        internal Target UpdateAlternativeNames(Target saved, Target match)
        {
            // Add/remove alternative names
            var addedNames = match.AlternativeNames.Except(saved.AlternativeNames);
            var removedNames = saved.AlternativeNames.Except(match.AlternativeNames);
            if (addedNames.Count() > 0)
            {
                Program.Log.Warning("- Added host(s): {names}", string.Join(", ", addedNames));
            }
            if (removedNames.Count() > 0)
            {
                Program.Log.Warning("- Removed host(s): {names}", string.Join(", ", removedNames));
            }
            saved.AlternativeNames = match.AlternativeNames;
            return saved;
        }
    }
}