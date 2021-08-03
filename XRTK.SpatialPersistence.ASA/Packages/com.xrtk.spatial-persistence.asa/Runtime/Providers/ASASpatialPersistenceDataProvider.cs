// Copyright (c) XRTK. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Azure.SpatialAnchors;
using Microsoft.Azure.SpatialAnchors.Unity;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using XRTK.Definitions;
using XRTK.Definitions.SpatialPersistence;
using XRTK.Definitions.Utilities;
using XRTK.Interfaces.SpatialPersistence;
using XRTK.Services;

namespace XRTK.Providers.SpatialPersistence
{
    [System.Runtime.InteropServices.Guid("02963BCE-8519-4923-AE59-833953F6F13C")]
    public class ASASpatialPersistenceDataProvider : BaseDataProvider, IMixedRealitySpatialPersistenceDataProvider
    {
        private readonly IMixedRealitySpatialPersistenceSystem spatialPersistenceSystem = null; 
        private SpatialAnchorManager cloudManager;
        private AnchorLocateCriteria anchorLocateCriteria;
        private CloudSpatialAnchorWatcher currentWatcher;

        private Dictionary<Guid, CloudSpatialAnchor> detectedAnchors = new Dictionary<Guid, CloudSpatialAnchor>();

        /// <inheritdoc />
        public SystemType SpatialPersistenceType => typeof(ASASpatialPersistenceDataProvider);

        /// <inheritdoc />
        public bool IsRunning => cloudManager != null && cloudManager.IsSessionStarted;

        public ASASpatialPersistenceDataProvider(string name, uint priority, SpatialPersistenceDataProviderProfile profile, IMixedRealitySpatialPersistenceSystem parentService)
            : base(name, priority, profile, parentService)
        {
            spatialPersistenceSystem = parentService;
        }

        #region BaseExtensionService Implementation

        /// <inheritdoc />
        public override void Initialize()
        {
            base.Initialize();

            // Get a reference to the SpatialAnchorManager component (must be on the same gameobject)
            cloudManager = GameObject.FindObjectOfType<SpatialAnchorManager>();
            if (cloudManager == null)
            {
                cloudManager = GameObject.FindObjectOfType<ARSessionOrigin>().gameObject.AddComponent<SpatialAnchorManager>();
            }
            if (cloudManager == null)
            {
                var message = $"Unable to locate either the {typeof(SpatialAnchorManager)} or {typeof(ARSession)} in the sceve, service cannot initialize";
                Debug.LogError(message);
                OnSpatialPersistenceError(message);
            }

            spatialPersistenceSystem.RegisterSpatialPersistenceDataProvider(this);

            OnSessionInitialised();
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

        #endregion

        #region IMixedRealitySpatialPersistenceDataProvider Implementation

        /// <inheritdoc />
        public async void StartSpatialPersistenceProvider()
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

                OnSessionStarted();
            }
            else
            {
                OnSpatialPersistenceError("Unable to start the Spatial Persistence provider, is it configuired correctly?");
            }
        }

        /// <inheritdoc />
        public async void StopSpatialPersistenceProvider()
        {
            if (currentWatcher != null)
            {
                currentWatcher.Stop();
                currentWatcher = null;
            }

            // Stops any existing session
            cloudManager.StopSession();

            // Resets the current session if there is one, and waits for any active queries to be stopped
            await cloudManager.ResetSessionAsync();

            OnSessionEnded();
        }

        /// <summary>
        /// Anchor located by the ASA Cloud watcher service, returns the ID reported by the service for the anchor via <see cref="OnAnchorLocated"/> event
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

                Pose detectedAnchorPose = Pose.identity;

                //Android and iOS require coordinate from stored Anchor
#if UNITY_ANDROID || UNITY_IOS
                detectedAnchorPose = detectedAnchors[anchorGuid].GetPose();
#endif

                var anchoredObject = new GameObject($"Anchor - [{anchorGuid}]");
                anchoredObject.transform.SetPositionAndRotation(detectedAnchorPose.position, detectedAnchorPose.rotation);

                CloudNativeAnchor attachedAnchor = GetClouddNativeAnchor(anchoredObject);

                attachedAnchor.CloudToNative(detectedAnchors[anchorGuid]);

                OnAnchorLocated(anchorGuid, anchoredObject);
            }
            else
            {
                OnSpatialPersistenceError($"Anchor returned from service but Identifier was invalid [{args.Identifier}]");
            }
        }

        /// <inheritdoc />
        public async void TryCreateAnchor(Vector3 position, Quaternion rotation, DateTimeOffset timeToLive)
        {
            if (cloudManager == null || !cloudManager.IsSessionStarted)
            {
                OnSpatialPersistenceError("Unable to create Anchor as the Spatial Persistence provider is not running, is it configuired correctly?");
                return;
            }

            OnCreateAnchoredObjectStarted();

            var anchoredObject = new GameObject("Anchor");
            anchoredObject.transform.SetPositionAndRotation(position, rotation);

            CloudNativeAnchor cloudNativeAnchor = GetClouddNativeAnchor(anchoredObject);

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
                float createProgress = cloudManager.SessionStatus.RecommendedForCreateProgress;
                var message = $"Move your device to capture more environment data: {createProgress:0%}";
                OnSpatialPersistenceStatusMessage(message);
            }

            try
            {
                // Actually save
                await cloudManager.CreateAnchorAsync(cloudAnchor);

                if (cloudAnchor != null && Guid.TryParse(cloudAnchor.Identifier, out var cloudAnchorGuid))
                {
                    detectedAnchors.Add(cloudAnchorGuid, cloudAnchor);
                    anchoredObject.name = $"Anchor - [{cloudAnchor.Identifier}]";
                    OnCreateAnchorSucceeded(cloudAnchorGuid, anchoredObject);
                }
                else
                {
                    OnCreateAnchoredObjectFailed();
                }
                cloudAnchor = null;
            }
            catch (Exception ex)
            {
                OnSpatialPersistenceError(ex.ToString());
            }
        }

        /// <inheritdoc />
        public bool TryFindAnchorPoints(params Guid[] ids)
        {
            Debug.Assert(ids != null, "ID array is null");
            Debug.Assert(ids.Length < 1, "No Ids found to locate");

            if (ids != null)
            {
                OnFindAnchorStarted();

                anchorLocateCriteria.Identifiers = ids.ToStringArray();
                if ((cloudManager != null) && (cloudManager.Session != null))
                {
                    currentWatcher = cloudManager.Session.CreateWatcher(anchorLocateCriteria);
                    return true;
                }
                else
                {
                    currentWatcher = null;
                }
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
                Debug.Assert(attachedAnchor != null, "No existing ASA Anchor to move");

                return false;
            }

            //If the ASA Provider is not running, expose an error.
            if (cloudAnchorID != Guid.Empty && (cloudManager == null || !cloudManager.IsSessionStarted))
            {
                OnSpatialPersistenceError("Unable to create Anchor as the Spatial Persistence provider is not running, is it configuired correctly?");
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

            OnAnchorUpdated(cloudAnchorID, anchoredObject);
            return true;
        }

        /// <inheritdoc />
        public async void DeleteAnchors(params Guid[] ids)
        {
            Debug.Assert(ids != null, "ID array is null");
            Debug.Assert(ids.Length < 1, "No Ids found to delete");

            if (ids != null)
            {
                for (int i = 0; i < ids.Length; i++)
                {
                    if (detectedAnchors.ContainsKey(ids[i]))
                    {
                        await cloudManager.DeleteAnchorAsync(detectedAnchors[ids[i]]);
                        detectedAnchors.Remove(ids[i]);
                        OnAnchorDeleted(ids[i]);
                    }
                }
            }
        }

        /// <inheritdoc />
        public bool TryClearAnchorCache()
        {
            detectedAnchors = new Dictionary<Guid, CloudSpatialAnchor>();

            if (detectedAnchors?.Count == 0)
            {
                return true;
            }

            return false;
        }

        #endregion

        #region Events

        #region Provider Events

        /// <inheritdoc />
        public event Action SessionInitialised;
        public void OnSessionInitialised() => SessionInitialised?.Invoke();

        /// <inheritdoc />
        public event Action SessionStarted;
        public void OnSessionStarted() => SessionStarted?.Invoke();

        /// <inheritdoc />
        public event Action SessionEnded;
        public void OnSessionEnded() => SessionEnded?.Invoke();

        /// <inheritdoc />
        public event Action CreateAnchorStarted;
        public void OnCreateAnchoredObjectStarted() => CreateAnchorStarted?.Invoke();

        /// <inheritdoc />
        public event Action FindAnchorStarted;
        public void OnFindAnchorStarted() => FindAnchorStarted?.Invoke();

        /// <inheritdoc />
        public event Action<Guid> AnchorDeleted;
        public void OnAnchorDeleted(Guid id) => AnchorDeleted?.Invoke(id);

        #endregion Provider Events

        #region Service Events

        /// <inheritdoc />
        public event Action CreateAnchorFailed;
        public void OnCreateAnchoredObjectFailed() => CreateAnchorFailed?.Invoke();

        /// <inheritdoc />
        public event Action<Guid, GameObject> CreateAnchorSucceeded;
        public void OnCreateAnchorSucceeded(Guid id, GameObject anchoredObject) => CreateAnchorSucceeded?.Invoke(id, anchoredObject);

        /// <inheritdoc />
        public event Action<string> SpatialPersistenceStatusMessage;
        public void OnSpatialPersistenceStatusMessage(string message) => SpatialPersistenceStatusMessage?.Invoke(message);

        /// <inheritdoc />
        public event Action<string> SpatialPersistenceError;
        public void OnSpatialPersistenceError(string exception) => SpatialPersistenceError?.Invoke(exception);

        /// <inheritdoc />
        public event Action<Guid, GameObject> AnchorUpdated;
        public void OnAnchorUpdated(Guid id, GameObject gameObject) => AnchorUpdated?.Invoke(id, gameObject);

        /// <inheritdoc />
        public event Action<Guid, GameObject> AnchorLocated;
        public void OnAnchorLocated(Guid id, GameObject anchoredGameObject) => AnchorLocated?.Invoke(id, anchoredGameObject);

        #endregion Service Events

        #endregion Events

        #region Utilities

        /// <summary>
        /// Intrinsic function to get the <see cref="CloudNativeAnchor"/> for an attached Anchor and to add one if not found
        /// </summary>
        /// <param name="objectToAnchor">GameObject to verify and add a <see cref="CloudNativeAnchor"/> to if necessary</param>
        /// <returns>GameObject with a <see cref="CloudNativeAnchor"/> component attached</returns>
        private static CloudNativeAnchor GetClouddNativeAnchor(GameObject objectToAnchor)
        {
            var attachedAnchor = objectToAnchor.GetComponent<CloudNativeAnchor>();
            if (attachedAnchor == null)
            {
                attachedAnchor = objectToAnchor.AddComponent<CloudNativeAnchor>();
            }

            return attachedAnchor;
        }

        #endregion
    }

    public static class ArrayExtensions
    {
        public static string[] ToStringArray(this Guid[] input)
        {
            var newArray = new string[input.Length];
            for (var i = 0; i < input.Length; i++)
            {
                newArray[i] = input[i].ToString();
            }
            return newArray;
        }
    }
}
