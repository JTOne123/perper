using System;
using System.Threading.Tasks;
using Apache.Ignite.Core;
using Apache.Ignite.Core.DataStructures;

namespace Perper.Fabric.Services
{
    public class StreamServiceDeployment : IAsyncDisposable
    {
        private readonly IIgnite _ignite;
        private readonly string _name;

        private IAtomicReference<string> _refCountNameReference;

        public StreamServiceDeployment(IIgnite ignite, string name)
        {
            _ignite = ignite;
            _name = name;
        }

        public async ValueTask DeployAsync()
        {
            if (_refCountNameReference == null)
            {
                _refCountNameReference = _ignite.GetAtomicReference(_name, Guid.NewGuid().ToString(), true);
            }
            else
            {
                throw new InvalidOperationException();
            }

            var refCountName = _refCountNameReference.Read();
            var refCount = _ignite.GetAtomicLong(refCountName, default, false);

            if (refCount == null)
            {
                await DeployAsync(refCountName);
            }
            else
            {
                try
                {
                    if (refCount.Increment() == 1)
                    {
                        refCount.Close();
                    }
                }
                catch
                {
                    if (!refCount.IsClosed())
                    {
                        throw;
                    }
                }

                if (refCount.IsClosed())
                {
                    refCountName = Guid.NewGuid().ToString();
                    _refCountNameReference.Write(refCountName);
                    await DeployAsync(refCountName);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_refCountNameReference != null)
            {
                var refCountName = _refCountNameReference.Read();
                var refCount = _ignite.GetAtomicLong(refCountName, default, false);
                if (refCount.Decrement() == 0)
                {
                    refCount.Close();
                    await _ignite.GetServices().CancelAsync(refCountName);
                }
            }
        }

        private async Task DeployAsync(string refCountName)
        {
            _ignite.GetAtomicLong(refCountName, 1, true);

            var service = new StreamService {StreamObjectTypeName = _name};
            await _ignite.GetServices().DeployClusterSingletonAsync(refCountName, service);
        }
    }
}