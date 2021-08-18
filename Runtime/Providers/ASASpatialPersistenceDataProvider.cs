// Copyright (c) XRTK. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

#if UNITY_ANDROID || UNITY_IOS || UNITY_WSA

using Microsoft.Azure.SpatialAnchors;
using Microsoft.Azure.SpatialAnchors.Unity;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using XRTK.Definitions;
using XRTK.Definitions.SpatialPersistence;
using XRTK.Extensions;
using XRTK.Interfaces.SpatialPersistence;
using XRTK.Services;
using Object = UnityEngine.Object;

namespace XRTK.Providers.SpatialPersistence
{
    [System.Runtime.InteropServices.Guid("02963BCE-8519-4923-AE59-833953F6F13C")]
    public class ASASpatialPersistenceDataProvider : BaseDataProvider, IMixedRealitySpatialPersistenceDataProvider
    {
        private readonly IMixedRealitySpatialPersistenceSystem spatialPersistenceSystem = null;
        private readonly Dictionary<Guid, CloudSpatialAnchor> detectedAnchors = new Dictionary<Guid, CloudSpatialAnchor>();

        private SpatialAnchorManager cloudManager;
        private AnchorLocateCriteria anchorLocateCriteria;
        private CloudSpatialAnchorWatcher currentWatcher;

        /// <inheritdoc />
        public bool IsRunning => cloudManager != null && cloudManager.IsSessionStarted;

        public ASASpatialPersistenceDataProvider(string name, uint priority, BaseMixedRealityProfile profile, IMixedRealitySpatialPersistenceSystem parentService)
            : base(name, priority, null, parentService)
        {
            spatialPersistenceSystem = parentService;
        }

        #region BaseExtensionService Implementation

        /// <inheritdoc />
        public override void Initialize()
        {
            base.Initialize();

            // Get a reference to the SpatialAnchorManager component (must be on the same GameObject)
            cloudManager = Object.FindObjectOfType<SpatialAnchorManager>();

            if (cloudManager == null)
            {
                cloudManager = Object.FindObjectOfType<ARAnchorManager>().gameObject.AddComponent<SpatialAnchorManager>();
            }

            if (cloudManager == null)
            {
                var message = $"Unable to locate either the {typeof(SpatialAnchorManager)} or {typeof(ARSession)} in the scene, service cannot initialize";
                Debug.LogError(message);
                SpatialPersistenceError?.Invoke(message);
            }

            spatialPersistenceSystem.RegisterSpatialPersistenceDataProvider(this);
            SessionInitialized?.Invoke();
        }

        /// <inheritdoc />
        public override void Destroy()
        {
            if (cloudManager != null && cloudManager.Session != null)
            {
                cloudManager.DestroySession();
            }

            if (currentWatcher != null)
            {
                currentWatcher.Stop();
                currentWatcher = null;
            }

            base.Destroy();

            spatialPersistenceSystem.RegisterSpatialPersistenceDataProvider(this);
        }

        #endregion BaseExtensionService Implementation

        #region IMixedRealitySpatialPersistenceDataProvider Implementation

        /// <inheritdoc />
        public void StartSpatialPersistenceProvider()
        {
#if UNITY_WSA
            StartASASession();
#else
            if (ARSession.state == ARSessionState.SessionTracking)
            {
                StartSession();
            }
            else
            {
                ARSession.stateChanged += ARSession_stateChanged;
            }
#endif
        }

        private async void StartSession()
        {
            if (cloudManager.Session == null)
            {
                // Creates a new session if one does not exist
                await cloudManager.CreateSessionAsync();
            }

            await cloudManager.StartSessionAsync();

            if (cloudManager.IsSessionStarted)
            {
                anchorLocateCriteria = new AnchorLocateCriteria();

                // Register for Azure Spatial Anchor events
                cloudManager.AnchorLocated += CloudManager_AnchorLocated;
                SessionStarted?.Invoke();
            }
            else
            {
                SpatialPersistenceError?.Invoke("Unable to start the Spatial Persistence provider, is it configured correctly?");
            }
        }

        private void ARSession_stateChanged(ARSessionStateChangedEventArgs obj)
        {
            if (obj.state == ARSessionState.SessionTracking && !IsRunning)
            {
                StartSession();
            }
        }

        /// <inheritdoc />
        public async void StopSpatialPersistenceProvider()
        {
            if (cloudManager == null) { return; }

            if (currentWatcher != null)
            {
                currentWatcher.Stop();
                currentWatcher = null;
            }

            if (cloudManager.Session != null)
            {
                // Stops any existing session
                cloudManager.StopSession();

                // Resets the current session if there is one, and waits for any active queries to be stopped
                await cloudManager.ResetSessionAsync();
                SessionEnded?.Invoke();
            }
        }

        /// <summary>
        /// Anchor located by the ASA Cloud watcher service, returns the ID reported by the service for the anchor via <see cref="AnchorLocated"/> event
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        private void CloudManager_AnchorLocated(object sender, AnchorLocatedEventArgs args)
        {
            if (Guid.TryParse(args.Identifier, out var anchorGuid))
            {
                if (!detectedAnchors.ContainsKey(anchorGuid))
                {
                    detectedAnchors.Add(anchorGuid, args.Anchor);
                }

                //Android and iOS require coordinate from stored Anchor
#if UNITY_ANDROID || UNITY_IOS
                var detectedAnchorPose = detectedAnchors[anchorGuid].GetPose();
#else
                var detectedAnchorPose = Pose.identity;
#endif
                var anchoredObject = new GameObject($"Anchor - [{anchorGuid}]");
                anchoredObject.transform.SetPositionAndRotation(detectedAnchorPose.position, detectedAnchorPose.rotation);

                var attachedAnchor = anchoredObject.EnsureComponent<CloudNativeAnchor>();
                attachedAnchor.CloudToNative(detectedAnchors[anchorGuid]);

                AnchorLocated?.Invoke(anchorGuid, anchoredObject);
            }
            else
            {
                SpatialPersistenceError?.Invoke($"Anchor returned from service but Identifier was invalid [{args.Identifier}]");
            }
        }

        /// <inheritdoc />
        public async void TryCreateAnchor(Vector3 position, Quaternion rotation, DateTimeOffset timeToLive)
        {
            if (cloudManager == null || !cloudManager.IsSessionStarted)
            {
                SpatialPersistenceError?.Invoke("Unable to create Anchor as the Spatial Persistence provider is not running, is it configured correctly?");
                return;
            }

            CreateAnchorStarted?.Invoke();

            var anchoredObject = new GameObject(nameof(CloudNativeAnchor));
            anchoredObject.transform.SetPositionAndRotation(position, rotation);
            var cloudNativeAnchor = anchoredObject.EnsureComponent<CloudNativeAnchor>();

            // If the cloud portion of the anchor hasn't been created yet, create it
            if (cloudNativeAnchor.CloudAnchor == null)
            {
                await cloudNativeAnchor.NativeToCloud();
            }

            // Get the cloud portion of the anchor
            CloudSpatialAnchor cloudAnchor = cloudNativeAnchor.CloudAnchor;

            // In this sample app we delete the cloud anchor explicitly, but here we show how to set an anchor to expire automatically
            cloudAnchor.Expiration = timeToLive;

            // Save anchor to cloud
            while (!cloudManager.IsReadyForCreate)
            {
                await Task.Delay(330);
                var createProgress = cloudManager.SessionStatus.RecommendedForCreateProgress;
                var message = $"{createProgress:0%}";
                SpatialPersistenceStatusMessage?.Invoke(message);
            }

            try
            {
                // Actually save
                await cloudManager.CreateAnchorAsync(cloudAnchor);

                if (cloudAnchor != null && Guid.TryParse(cloudAnchor.Identifier, out var cloudAnchorGuid))
                {
                    detectedAnchors.Add(cloudAnchorGuid, cloudAnchor);
                    anchoredObject.name = $"Cloud Anchor [{cloudAnchor.Identifier}]";
                    CreateAnchorSucceeded?.Invoke(cloudAnchorGuid, anchoredObject);
                }
                else
                {
                    CreateAnchorFailed?.Invoke();
                }
            }
            catch (Exception ex)
            {
                SpatialPersistenceError?.Invoke($"{ex}");
            }
        }

        /// <inheritdoc />
        public bool TryFindAnchorPoints(params Guid[] ids)
        {
            Debug.Assert(ids != null, "ID array is null");
            Debug.Assert(ids.Length < 1, "No Ids found to locate");

            if (ids.Length > 0)
            {
                FindAnchorStarted?.Invoke();
                anchorLocateCriteria.Identifiers = ids.ToStringArray();

                if ((cloudManager != null) && (cloudManager.Session != null))
                {
                    currentWatcher = cloudManager.Session.CreateWatcher(anchorLocateCriteria);
                    return true;
                }

                currentWatcher = null;
            }

            return false;
        }

        /// <inheritdoc />
        public bool TryFindAnchorPoints(SpatialPersistenceSearchType searchType)
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc />
        public bool HasCloudAnchor(GameObject anchoredObject)
        {
            Debug.Assert(anchoredObject != null, "Anchored Object is null");

            var cloudAnchor = anchoredObject.GetComponent<CloudNativeAnchor>();
            return cloudAnchor != null && !string.IsNullOrEmpty(cloudAnchor.CloudAnchor.Identifier);
        }

        /// <inheritdoc />
        public bool TryMoveSpatialPersistence(GameObject anchoredObject, Vector3 position, Quaternion rotation, Guid cloudAnchorID = new Guid())
        {
            Debug.Assert(anchoredObject != null, "Anchored Object is null");

            var attachedAnchor = anchoredObject.GetComponent<CloudNativeAnchor>();

            if (attachedAnchor == null)
            {
                Debug.Assert(attachedAnchor != null, $"No existing {nameof(CloudNativeAnchor)} to move");
                return false;
            }

            //If the ASA Provider is not running, expose an error.
            if (cloudAnchorID != Guid.Empty && (cloudManager == null || !cloudManager.IsSessionStarted))
            {
                SpatialPersistenceError?.Invoke("Unable to create Anchor as the Spatial Persistence provider is not running, is it configured correctly?");
                return false;
            }

            // if a Cloud identifier is provided and the corresponding ID has been found, move object to anchored point.
            // Else force move the anchor which breaks any preexisting cloud anchor reference.
            if (cloudAnchorID != Guid.Empty && detectedAnchors.ContainsKey(cloudAnchorID))
            {
                attachedAnchor.CloudToNative(detectedAnchors[cloudAnchorID]);
            }
            else
            {
                attachedAnchor.SetPose(position, rotation);
            }

            AnchorUpdated?.Invoke(cloudAnchorID, anchoredObject);
            return true;
        }

        /// <inheritdoc />
        public async void DeleteAnchors(params Guid[] ids)
        {
            Debug.Assert(ids != null, "ID array is null");
            Debug.Assert(ids.Length < 1, "No Ids found to delete");

            if (ids.Length > 0)
            {
                for (int i = 0; i < ids.Length; i++)
                {
                    if (detectedAnchors.ContainsKey(ids[i]))
                    {
                        await cloudManager.DeleteAnchorAsync(detectedAnchors[ids[i]]);
                        detectedAnchors.Remove(ids[i]);
                        AnchorDeleted?.Invoke(ids[i]);
                    }
                }
            }
        }

        /// <inheritdoc />
        public bool TryClearAnchorCache()
        {
            detectedAnchors.Clear();
            return detectedAnchors?.Count == 0;
        }

        #region Events

        #region Provider Events

        /// <inheritdoc />
        public event Action SessionInitialized;

        /// <inheritdoc />
        public event Action SessionStarted;

        /// <inheritdoc />
        public event Action SessionEnded;

        /// <inheritdoc />
        public event Action CreateAnchorStarted;

        /// <inheritdoc />
        public event Action FindAnchorStarted;

        /// <inheritdoc />
        public event Action<Guid> AnchorDeleted;

        #endregion Provider Events

        #region Service Events

        /// <inheritdoc />
        public event Action CreateAnchorFailed;

        /// <inheritdoc />
        public event Action<Guid, GameObject> CreateAnchorSucceeded;

        /// <inheritdoc />
        public event Action<string> SpatialPersistenceStatusMessage;

        /// <inheritdoc />
        public event Action<string> SpatialPersistenceError;

        /// <inheritdoc />
        public event Action<Guid, GameObject> AnchorUpdated;

        /// <inheritdoc />
        public event Action<Guid, GameObject> AnchorLocated;

        #endregion Service Events

        #endregion Events

        #endregion IMixedRealitySpatialPersistenceDataProvider Implementation
    }
}

#endif