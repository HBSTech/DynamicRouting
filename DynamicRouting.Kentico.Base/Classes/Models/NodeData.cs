﻿using CMS.DataEngine;
using CMS.DocumentEngine;
using CMS.EventLog;
using CMS.Helpers;
using CMS.Localization;
using CMS.MacroEngine;
using CMS.SiteProvider;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace DynamicRouting
{
    /// <summary>
    /// A NodeItem is a representation of a Node's Url Slugs and any children.  Used to build Url Slugs and save.
    /// </summary>
    public class NodeItem
    {
        public int NodeID;
        public List<NodeUrlSlug> UrlSlugs { get; set; }
        public bool IsContainer { get; set; }

        public bool SavedAlready { get; set; } = false;

        public string ClassName { get; set; }

        public int AlsoBuildNodeID { get; set; } = -1;

        // If any of the Slugs were either new (no existing node slug guid) or they are updated, then updates occurred
        public bool HasUpdates
        {
            get
            {
                return UrlSlugs.Exists(x => ValidationHelper.GetGuid(x.ExistingNodeSlugGuid, Guid.Empty) == Guid.Empty || x.IsNewOrUpdated);
            }
        }

        public NodeItemBuilderSettings Settings { get; set; }

        public NodeItem Parent { get; set; }

        public List<NodeItem> Children { get; set; }

        public bool ChildrenBuilt { get; set; } = false;

        /// <summary>
        /// For Serialization and Deserialization only
        /// </summary>
        public NodeItem()
        {
            UrlSlugs = new List<NodeUrlSlug>();
            Children = new List<NodeItem>();
        }

        /// <summary>
        /// This constructor should be called when a Node is updated, passing the NodeID and it's parent.  This is a starting point 
        /// </summary>
        /// <param name="ParentNodeID">The Parent Node id, this will be the primary root of the NodeItem Build</param>
        /// <param name="Settings">The Node Item Builder Settings</param>
        /// <param name="UseCurrentVersion">If the current version of the document should be used, should only be true if doing a Conflict check and not saving.</param>
        public NodeItem(int PageNodeID, NodeItemBuilderSettings Settings, bool UseCurrentVersion = false)
        {
            if (Settings.BuildSiblings)
            {
                // Start with Parent and build children, and children of the PageNodeID
                NodeID = DocumentHelper.GetDocuments().WhereEquals("NodeID", PageNodeID).CombineWithAnyCulture().FirstOrDefault().NodeParentID;
                if (NodeID <= 0)
                {
                    NodeID = PageNodeID;
                }
            }
            else
            {
                // Start with the node, and only build itself and it's children children.
                NodeID = PageNodeID;
            }

            TreeNode Node = DocumentHelper.GetDocuments().WhereEquals("NodeID", NodeID).CombineWithAnyCulture().FirstOrDefault();
            Parent = null;
            UrlSlugs = new List<NodeUrlSlug>();
            IsContainer = Node.IsCoupled;
            ClassName = Node.ClassName;
            Children = new List<NodeItem>();
            ChildrenBuilt = true;
            this.Settings = Settings;

            // Builds the slugs for itself
            BuildUrlSlugs(UseCurrentVersion);

            // This will possibly be something other than -1 only on the initial node based on settings.
            AlsoBuildNodeID = Settings.BuildSiblings ? PageNodeID : -1;
        }

        public NodeItem(NodeItem parent, TreeNode Node, NodeItemBuilderSettings Settings, bool BuildChildren = false)
        {
            NodeID = Node.NodeID;
            Parent = parent;
            UrlSlugs = new List<NodeUrlSlug>();
            Children = new List<NodeItem>();
            IsContainer = Node.IsCoupled;
            ClassName = Node.ClassName;
            this.Settings = Settings;

            // Build it's slugs
            BuildUrlSlugs();

            // If build children, or settings to build descendents, or if an update was found, build children
            if (BuildChildren || Settings.BuildDescendents || HasUpdates)
            {
                this.BuildChildren();
            }
        }

        /// <summary>
        /// Builds the child items
        /// </summary>
        /// <param name="AlsoBuildChildrenOfNodeID">The child that matches this NodeID will also have it's children built.</param>
        public void BuildChildren()
        {
            foreach (TreeNode Child in DocumentHelper.GetDocuments().WhereEquals("NodeParentID", NodeID).CombineWithAnyCulture().Columns("NodeID, CLassName, NodeClassID"))
            {
                Children.Add(new NodeItem(this, Child, Settings, AlsoBuildNodeID == Child.NodeID));
            }
            ChildrenBuilt = true;
        }

        /// <summary>
        /// Generates the Url slugs for itself, first pulling any Custom Url Slugs, then rendering all culture codes outlined in the settings
        /// <paramref name="UseCurrentVersion">If true, then will look to the current version of the document, used only in Checking for conflicts to block publishing</paramref>
        /// </summary>
        public void BuildUrlSlugs(bool UseCurrentVersion = false)
        {
            // Temp replace Method version with Normal version
            string Pattern = Regex.Replace(DynamicRouteInternalHelper.GetClass(ClassName).ClassURLPattern, "ParentUrl\\(\\)", "ParentUrl", RegexOptions.IgnoreCase);

            // if no pattern, then default to node alias path, this way any child with a ParentUrl will still have a value.
            if (string.IsNullOrWhiteSpace(Pattern))
            {
                Pattern = "{% NodeAliasPath %}";
            }

            var SlugQuery = UrlSlugInfoProvider.GetUrlSlugs()
                .Source(x => x.Join(new QuerySourceTable("CMS_Tree", null, new string[] { }), new WhereCondition("NodeID = UrlSlugNodeID"), JoinTypeEnum.Inner))
                .Columns(new string[] { "UrlSlugID", "UrlSlugGuid", "UrlSlugLastModified", "UrlSlug", "UrlSlugNodeID", "UrlSlugCultureCode", "UrlSlugIsCustom", "NodeSiteID" })
                .WhereEquals("UrlSlugNodeID", NodeID);

            // If not checking for updates (rebuild), then the only ones we want to keep are the Custom Url Slugs.
            if (!Settings.CheckingForUpdates)
            {
                SlugQuery.WhereEquals("UrlSlugIsCustom", true);
            }

            // Import the existing Slugs (Custom if not checking for imports, or all of them)
            foreach (UrlSlugInfo ExistingSlug in SlugQuery)
            {
                UrlSlugs.Add(new NodeUrlSlug()
                {
                    SiteID = ExistingSlug.GetIntegerValue("NodeSiteID", 1),
                    IsCustom = ExistingSlug.UrlSlugIsCustom,
                    IsDefault = ExistingSlug.UrlSlugCultureCode.Equals(Settings.DefaultCultureCode, StringComparison.InvariantCultureIgnoreCase),
                    CultureCode = ExistingSlug.UrlSlugCultureCode,
                    UrlSlug = ExistingSlug.UrlSlug,
                    ExistingNodeSlugGuid = ExistingSlug.UrlSlugGuid
                });
            }

            // Go through any cultures that do not have custom slugs already, these are the cultures that need to be rebuilt
            foreach (string CultureCode in Settings.CultureCodes.Where(culture => !UrlSlugs.Exists(slug => slug.IsCustom && slug.CultureCode.Equals(culture, StringComparison.InvariantCultureIgnoreCase))))
            {
                var CultureResolver = Settings.BaseResolver.CreateChild();
                CultureResolver.SetAnonymousSourceData(new object[] { DynamicRouteInternalHelper.GetCulture(CultureCode) });

                bool IsDefaultCulture = CultureCode.Equals(Settings.DefaultCultureCode, StringComparison.InvariantCultureIgnoreCase);

                // Get actual Document, if it's the default culture, it MUST return some document, no matter what.
                var DocQuery = DocumentHelper.GetDocuments(ClassName)
                    .WhereEquals("NodeID", NodeID)
                    .Culture(CultureCode)
                    .CombineWithDefaultCulture(IsDefaultCulture);

                if (UseCurrentVersion)
                {
                    DocQuery.LatestVersion(true).Published(false);
                }
                else
                {
                    DocQuery.PublishedVersion(true);
                }

                TreeNode Document = DocQuery.FirstOrDefault();
                // If the Document is either Null because there is no Default Culture, then get any document
                // and set the culture code if the current Culture checking is either the default culture (required)
                // or is another culture but should be generated due to the GenerateIfCultureDoesntExist setting
                if (Document == null && (IsDefaultCulture || Settings.GenerateIfCultureDoesntExist))
                {
                    var AnyCultureDocQuery = DocumentHelper.GetDocuments(ClassName)
                        .WhereEquals("NodeID", NodeID)
                        .CombineWithAnyCulture();
                    if (UseCurrentVersion)
                    {
                        AnyCultureDocQuery.LatestVersion(true).Published(false);
                    }
                    else
                    {
                        AnyCultureDocQuery.PublishedVersion(true);
                    }
                    Document = AnyCultureDocQuery.FirstOrDefault();
                    Document.DocumentCulture = CultureCode;
                }

                if (Document != null)
                {
                    // Add Document values and ParentUrl
                    var DocResolver = CultureResolver.CreateChild();
                    DocResolver.SetAnonymousSourceData(new object[] { Document });
                    if (Parent == null)
                    {
                        // Look up parent Url Slug
                        UrlSlugInfo ParentSlug = UrlSlugInfoProvider.GetUrlSlugs()
                            .WhereEquals("UrlSlugNodeID", Document.NodeParentID)
                            .OrderBy($"case when UrlSlugCultureCode = '{CultureCode}' then 0 else 1 end, case when UrlSlugCultureCode = '{Settings.DefaultCultureCode}' then 0 else 1 end, UrlSlugCultureCode")
                            .FirstOrDefault();
                        if (ParentSlug != null)
                        {
                            DocResolver.SetNamedSourceData("ParentUrl", ParentSlug.UrlSlug);
                        }
                        else
                        {
                            DocResolver.SetNamedSourceData("ParentUrl", "");
                        }
                    }
                    else
                    {
                        DocResolver.SetNamedSourceData("ParentUrl", Parent.GetUrlSlug(CultureCode));
                    }

                    var NodeSlug = new NodeUrlSlug()
                    {
                        SiteID = Document.NodeSiteID,
                        CultureCode = CultureCode,
                        IsCustom = false,
                        IsDefault = IsDefaultCulture,
                        UrlSlug = DynamicRouteInternalHelper.GetCleanUrl(DocResolver.ResolveMacros(Pattern), Settings.SiteName),
                    };

                    // If checking for updates, need to flag that an update was found
                    if (Settings.CheckingForUpdates)
                    {
                        var ExistingSlug = UrlSlugs.Where(x => x.CultureCode.Equals(CultureCode, StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();
                        if (ExistingSlug != null)
                        {
                            // Update the existing UrlSlug only if it is different and not custom
                            if (!ExistingSlug.UrlSlug.Equals(NodeSlug.UrlSlug))
                            {
                                ExistingSlug.IsNewOrUpdated = true;
                                ExistingSlug.PreviousUrlSlug = ExistingSlug.UrlSlug;
                                ExistingSlug.UrlSlug = NodeSlug.UrlSlug;
                            }
                        }
                        else
                        {
                            // No Slug exists for this culture, add.
                            NodeSlug.IsNewOrUpdated = true;
                            UrlSlugs.Add(NodeSlug);
                        }
                    }
                    else
                    {
                        // Not checking for updates to just adding node slug.
                        NodeSlug.IsNewOrUpdated = true;
                        UrlSlugs.Add(NodeSlug);
                    }
                }
                else if (!Settings.GenerateIfCultureDoesntExist && !IsDefaultCulture)
                {
                    // If document no longer exists but a Url slug exists, set this slug to be deleted.
                    var SlugToDelete = UrlSlugs.Where(x => x.ExistingNodeSlugGuid != null && x.CultureCode.Equals(CultureCode, StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();
                    if (SlugToDelete != null)
                    {
                        SlugToDelete.Delete = true;
                    }
                }
            }
        }

        /// <summary>
        /// Gets the Url Slug for the given culture, falling back if the culture isn't found.
        /// </summary>
        /// <param name="CultureCode">The Culture Code</param>
        /// <returns>The Url Slug</returns>
        public string GetUrlSlug(string CultureCode)
        {
            var UrlSlug = UrlSlugs.Where(x => x.CultureCode.Equals(CultureCode, StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();
            if (UrlSlug == null)
            {
                UrlSlug = UrlSlugs.Where(x => x.IsDefault).FirstOrDefault();
            }
            if (UrlSlug == null)
            {
                UrlSlug = UrlSlugs.FirstOrDefault();
            }
            return UrlSlug?.UrlSlug;
        }

        /// <summary>
        /// Returns a list of Conflicts for the given build.
        /// </summary>
        /// <returns>List of all the conflicing Url Slugs</returns>
        public List<string> GetConflictItems()
        {
            List<string> Conflicts = new List<string>();

            // Check itself for conflicts with UrlSlugs
            foreach (NodeUrlSlug UrlSlug in UrlSlugs)
            {
                if (UrlSlug.IsNewOrUpdated)
                {
                    // Check for existing Url Slug that matches the new Url
                    var MatchingUrlSlug = UrlSlugInfoProvider.GetUrlSlugs()
                        .WhereEquals("UrlSlug", UrlSlug.UrlSlug)
                        .WhereNotEquals("UrlSlugNodeID", NodeID)
                        .Where($"UrlSlugNodeID in (Select NodeID from CMS_Tree where NodeSiteID = {UrlSlug.SiteID})")
                        .FirstOrDefault();
                    if (MatchingUrlSlug != null)
                    {
                        // Get NodeAliasPaths
                        string MainNodePath = DocumentHelper.GetDocuments()
                            .WhereEquals("NodeID", NodeID)
                            .FirstOrDefault().NodeAliasPath;
                        string MatchNodePath = DocumentHelper.GetDocuments()
                            .WhereEquals("NodeID", MatchingUrlSlug.UrlSlugNodeID)
                            .FirstOrDefault().NodeAliasPath;
                        Conflicts.Add($"Conflict on UrlSlug [{UrlSlug.UrlSlug}] between NodeID {NodeID} [{MainNodePath}] and NodeID {MatchingUrlSlug.UrlSlugNodeID} [{MatchNodePath}]");
                    }
                }
            }

            // Now Call save children on children if they are built.
            foreach (NodeItem Child in Children)
            {
                if (Child.ConflictsExist())
                {
                    Conflicts.AddRange(Child.GetConflictItems());
                }
            }
            return Conflicts;
        }

        /// <summary>
        /// Checks if any children have Conflicts that would cause a UrlSlug to match two separate nodes
        /// </summary>
        /// <returns>True if Conflicts will arrise, false if no conflicts</returns>
        public bool ConflictsExist()
        {
            // Check itself for conflicts with UrlSlugs
            foreach (NodeUrlSlug UrlSlug in UrlSlugs)
            {
                if (UrlSlug.IsNewOrUpdated)
                {
                    // Check for existing Url Slug that matches the new Url
                    var MatchingUrlSlug = UrlSlugInfoProvider.GetUrlSlugs()
                        .WhereEquals("UrlSlug", UrlSlug.UrlSlug)
                        .WhereNotEquals("UrlSlugNodeID", NodeID)
                        .Where($"UrlSlugNodeID in (Select NodeID from CMS_Tree where NodeSiteID = {UrlSlug.SiteID})")
                        .FirstOrDefault();
                    if (MatchingUrlSlug != null)
                    {
                        return true;
                    }
                }
            }

            // Now Call save children on children if they are built.
            bool ConflictExists = false;
            foreach (NodeItem Child in Children)
            {
                ConflictExists = (ConflictExists || Child.ConflictsExist());
            }
            return ConflictExists;
        }

        /// <summary>
        /// Saves any inserts, updates or deletes to the database
        /// </summary>
        /// <param name="SaveChildren">If the children should be saved as well.</param>
        public void SaveChanges(bool SaveChildren = true)
        {

            bool ShouldSaveChildren = SaveChildren || SavedAlready;

            // Add catch for uniqueness across url and site, no duplicates, how to handle?  revert to node alias path with or without document culture?
            if (!SavedAlready)
            {
                // Check itself for changes and save, then children
                foreach (NodeUrlSlug UrlSlug in UrlSlugs)
                {
                    // Handle Deletes
                    if (UrlSlug.Delete)
                    {
                        UrlSlugInfoProvider.DeleteUrlSlugInfo(UrlSlug.ExistingNodeSlugGuid);
                        continue;
                    }
                    if (UrlSlug.IsNewOrUpdated)
                    {
                        // Check for existing Url Slug that matches the new Url
                        var MatchingUrlSlug = UrlSlugInfoProvider.GetUrlSlugs()
                            .WhereEquals("UrlSlug", UrlSlug.UrlSlug)
                            .WhereNotEquals("UrlSlugNodeID", NodeID)
                            .Where($"UrlSlugNodeID in (Select NodeID from CMS_Tree where NodeSiteID = {UrlSlug.SiteID})")
                            .FirstOrDefault();
                        if (MatchingUrlSlug != null)
                        {
                            if (DynamicRouteInternalHelper.AppendPostFixOnConflict())
                            {
                                bool ExistingFound = true;
                                int AppendCount = 0;
                                // Loop till no match found
                                while (ExistingFound)
                                {
                                    string PreviousAppendex = $"-({AppendCount})";
                                    AppendCount++;
                                    string NewAppendex = $"-({AppendCount})";
                                    if (UrlSlug.UrlSlug.Contains(PreviousAppendex))
                                    {
                                        UrlSlug.UrlSlug = UrlSlug.UrlSlug.Replace(PreviousAppendex, NewAppendex);
                                    }
                                    else
                                    {
                                        UrlSlug.UrlSlug += NewAppendex;
                                    }
                                    ExistingFound = UrlSlugInfoProvider.GetUrlSlugs()
                                        .WhereEquals("UrlSlug", UrlSlug.UrlSlug)
                                        .WhereNotEquals("UrlSlugNodeID", NodeID)
                                        .Where($"UrlSlugNodeID in (Select NodeID from CMS_Tree where NodeSiteID = {UrlSlug.SiteID})")
                                        .FirstOrDefault() != null;
                                }
                            }
                            else
                            {
                                // Technically should never hit this as the action should have either been cancelled or logged with an error
                                throw new Exception("Conflict Found on Save and Url Slug Conflict Behavior Setting is not 'Append Post Fix' so could not complete action.");
                            }
                        }

                        if (ValidationHelper.GetGuid(UrlSlug.ExistingNodeSlugGuid, Guid.Empty) != Guid.Empty)
                        {
                            // Now Update the Url Slug if it's not custom
                            var ExistingSlug = UrlSlugInfoProvider.GetUrlSlugInfo(UrlSlug.ExistingNodeSlugGuid);
                            if (!ExistingSlug.UrlSlugIsCustom)
                            {
                                ExistingSlug.UrlSlug = UrlSlug.UrlSlug;
                                UrlSlugInfoProvider.SetUrlSlugInfo(ExistingSlug);
                            }
                        }
                        else
                        {
                            var newSlug = new UrlSlugInfo()
                            {
                                UrlSlug = UrlSlug.UrlSlug,
                                UrlSlugNodeID = NodeID,
                                UrlSlugCultureCode = UrlSlug.CultureCode,
                                UrlSlugIsCustom = false
                            };
                            UrlSlugInfoProvider.SetUrlSlugInfo(newSlug);
                        }
                    }
                }
                // Mark this as saved already
                SavedAlready = true;
            }

            // Now Call save children on children if they are built.
            if (ShouldSaveChildren)
            {
                foreach (NodeItem Child in Children)
                {
                    Child.SaveChanges();
                }
            }
        }
    }
}
