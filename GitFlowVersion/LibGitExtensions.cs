namespace GitFlowVersion
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using LibGit2Sharp;

    public static class LibGitExtensions
    {
        static FieldInfo commitRepoField;
        static FieldInfo branchRepoField;

        static LibGitExtensions()
        {
            branchRepoField = typeof(Branch).GetField("repo", BindingFlags.Instance | BindingFlags.FlattenHierarchy | BindingFlags.NonPublic);
            commitRepoField = typeof(Commit).GetField("repo", BindingFlags.Instance | BindingFlags.FlattenHierarchy | BindingFlags.NonPublic);
        }

        public static Repository Repository(this Branch branch)
        {
            return (Repository)branchRepoField.GetValue(branch);
        }

        public static IEnumerable<Commit> SpecificCommits(this Branch branch)
        {
            var firstCommitOnBranch = branch.Repository()
                .Refs
                .Log(branch.CanonicalName)
                .Last();
            foreach (var commit in branch.Commits)
            {
                if (commit.Id == firstCommitOnBranch.To)
                {
                    yield return commit;
                    break;
                }
                yield return commit;
            }
        }




        public static Repository Repository(this Commit commit)
        {
            return (Repository)commitRepoField.GetValue(commit);
        }
        public static DateTimeOffset When(this Commit commit)
        {
            return commit.Committer.When;
        }
        public static IEnumerable<Tag> CommitTags(this Repository repository)
        {
            return repository.Tags.Where(tag => tag.Target is Commit);
        }
        public static IEnumerable<Tag> Tags(this Commit commit)
        {
            return commit.Repository().Tags.Where(tag => tag.Target == commit);
        }
        public static IEnumerable<Tag> SemVerTags(this Commit commit)
        {
            return commit.Tags().Where(tag => VersionInformation.IsVersion(tag.Name));
        }
        public static IEnumerable<Reference> LocalBranchRefs(this Repository repository)
        {
            return repository.Refs.Where(r => r.IsLocalBranch());
        }
        public static IEnumerable<Reference> TagRefs(this Repository repository)
        {
            return repository.Refs.Where(r => r.IsTag());
        }
        public static Reference ToReference(this Branch branch)
        {
            return branch.Repository().Refs.First(x => x.CanonicalName == branch.CanonicalName);
        }
        public static bool IsOnBranch(this Commit commit, Branch branch)
        {
            return branch.Repository().Refs.ReachableFrom(new[] { branch.ToReference() }, new[] { commit }).Any();
        }
        public static VersionPoint GetVersionPriorTo(this Branch branch, DateTimeOffset olderThan)
        {
            foreach (var source in branch.Commits.SkipWhile(c => c.When() > olderThan))
            {
                if (source.Message.StartsWith("merge"))
                {
                    return new VersionPoint
                           {
                               Version = MergeMessageParser.GetVersionFromMergeCommit(source.Message),
                               Timestamp = source.When()
                           };
                }
                var semVerTag = source.SemVerTags().FirstOrDefault();
                if (semVerTag != null)
                {
                    return new VersionPoint
                    {
                        Version = MergeMessageParser.GetVersionFromMergeCommit(semVerTag.Name),
                        Timestamp = source.When()
                    };
                }
            }
            return new VersionPoint
                   {
                       Version = "0.1.0",
                       Timestamp = DateTimeOffset.MinValue
                   };
        }


        public static bool IsOnBranch(this Tag tag, Branch branch)
        {
            var commit = tag.Target as Commit;
            if (commit == null)
            {
                return false;
            }
            return commit.IsOnBranch(branch);
        }
        public static DateTimeOffset CommitTimeStamp(this Tag tag)
        {
            var commit = tag.Target as Commit;
            if (commit == null)
            {
                throw new InvalidOperationException();
            }
            return commit.When();
        }
        public static bool IsBefore(this Tag tag, Commit commit)
        {
            var tagCommit = tag.Target as Commit;
            if (tagCommit == null)
            {
                throw new InvalidOperationException();
            }
            return tagCommit.When() <= commit.When();
        }

        public static Branch GetBranch(this Repository repository, string name)
        {
            var branch = repository.Branches.FirstOrDefault(b => b.Name == name);

            if (branch == null)
            {
               
                if (!repository.Network.Remotes.Any())
                {
                    Logger.Write("No remotes found");
                }
                else
                {
                    var remote = repository.Network.Remotes.First();

                    Logger.Write(string.Format("No local branch with name {0} found, going to try on the remote {1}({2})", name, remote.Name, remote.Url));
                    try
                    {
                        repository.Network.Fetch(remote);
                    }
                    catch (LibGit2SharpException exception)
                    {
                        if (exception.Message.Contains("This transport isn't implemented"))
                        {
                            var message = string.Format("Could not fetch from '{0}' since LibGit2 does not support the transport. You have most likely cloned using SSH. If there is a remote branch named '{1}' then fetch it manually, otherwise please create a local branch named '{1}'.", remote.Url, name);
                            throw new MissingBranchException(message, exception);
                        }
                        throw;
                    }

                    branch = repository.Branches.FirstOrDefault(b => b.Name.EndsWith("/" + name));
                }
            }

            if (branch == null)
            {
                var branchNames = string.Join(";", repository.Branches);
                var message = string.Format("Could not find branch '{0}' in the repository, please create one. Existing branches:{1}", name, branchNames);
                throw new Exception(message);
            }

            return branch;
        }

        public static Branch DevelopBranch(this Repository repository)
        {
            return repository.GetBranch("develop");
        }

        public static Branch MasterBranch(this Repository repository)
        {
            return repository.GetBranch("master");
        }
    }

    public class VersionPoint
    {
        public string Version;
        public DateTimeOffset Timestamp;
    }
}