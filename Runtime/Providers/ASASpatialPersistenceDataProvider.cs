// Copyright (c) XRTK. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Azure.SpatialAnchors;
using Microsoft.Azure.SpatialAnchors.Unity;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using XRTK.Definitions.SpatialPersistence;
using XRTK.Definitions.Utilities;
using XRTK.Interfaces;
using XRTK.Interfaces.SpatialPersistence;
using XRTK.Services;

namespace XRTK.Providers.SpatialPersistence
{
    [System.Runtime.InteropServices.Guid("bc7fc778-4ffd-4d36-a840-d2e86de303a8")]
    public class ASASpatialPersistenceDataProvider : BaseExtensionDataProvider, IMixedRealitySpatialPersistenceDataProvider
    {
        private SpatialAnchorManager cloudManager;
        private AnchorLocateCriteria anchorLocateCriteria;
        private CloudSpatialAnchorWatcher currentWatcher;
        private GameObject spatialManagerGameObject;

        private Dictionary<string, CloudSpatialAnchor> detectedAnchors = new Dictionary<string, CloudSpatialAnchor>();

        /// <inheritdoc />
        public SystemType SpatialPersistenceType => typeof(ASASpatialPersistenceDataProvider);

        /// <inheritdoc />
        public bool IsRunning => cloudManager != null && cloudManager.IsSessionStarted;

        public ASASpatialPersistenceDataProvider(string name, uint priority, SpatialPersistenceDataProviderProfile profile, IMixedRealityExtensionService parentService)
            : base(name, priority, profile, parentService)
        { }

        #region BaseExtensionService Implementation

        /// <inheritdoc />
        public override void Initialize()
        {
            base.Initialize();

            // Get a reference to the SpatialAnchorManager component (must be on the same gameobject)
            cloudManager = GameObject.FindObjectOfType<SpatialAnchorManager>();
            if (cloudManager == null)
            {
                cloudManager = GameObject.FindObjectOfType<ARSession>().gameObject.AddComponent<SpatialAnchorManager>();
            }
            if (cloudManager == null)
            {
                var message = $"Unable to locate either the {typeof(SpatialAnchorManager)} or {typeof(ARSession)} in the sceve, service cannot initialize";
                Debug.LogError(message);
                OnSpatialPersistenceError(message);
            }

            // Register for Azure Spatial Anchor events
            cloudManager.AnchorLocated += CloudManager_AnchorLocated;

            anchorLocateCriteria = new AnchorLocateCriteria();

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

            OnSessionStarted();
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
        /// Anchor located by the ASA Cloud watcher service, returns the ID reported by the service for the anchor via <see cref="OnCloudAnchorLocated"/> event
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        private void CloudManager_AnchorLocated(object sender, AnchorLocatedEventArgs args)
        {
            if (!detectedAnchors.ContainsKey(args.Identifier))
            {
                detectedAnchors.Add(args.Identifier, args.Anchor);
            }

            OnCloudAnchorLocated(args.Identifier);
        }

        /// <inheritdoc />
        public async void CreateAnchoredObject(GameObject objectToAnchorPrefab, Vector3 position, Quaternion rotation, DateTimeOffset timeToLive)
        {
            Debug.Assert(objectToAnchorPrefab != null, "Anchored Object Prefab is null");

            OnCreateAnchoredObjectStarted();

            var anchoredObject = GameObject.Instantiate(objectToAnchorPrefab, position, rotation);

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

                // Success?
                //bool success = ;
                if (cloudAnchor != null)
                {
                    detectedAnchors.Add(cloudAnchor.Identifier, cloudAnchor);
                    OnCreateAnchoredObjectSucceeded(cloudAnchor.Identifier, anchoredObject);
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
        public bool FindAnchorPoint(string id)
        {
            return FindAnchorPoints(new string[] { id });
        }

        /// <inheritdoc />
        public bool FindAnchorPoints(string[] ids)
        {
            Debug.Assert(ids != null, "ID array is null");
            Debug.Assert(ids.Length < 1, "No Ids found to locate");

            if (ids != null)
            {
                OnAnchorLocated();

                anchorLocateCriteria.Identifiers = ids;
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
        public virtual bool FindAnchorPoints(SpatialPersistenceSearchType searchType)
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc />
        public bool PlaceAnchoredObject(string id, GameObject objectToAnchorPrefab)
        {
            Debug.Assert(!string.IsNullOrEmpty(id), "Anchor ID is null");
            Debug.Assert(objectToAnchorPrefab != null, "Object To Anchor Prefab is null");

            if (detectedAnchors.ContainsKey(id))
            {
                Pose detectedAnchorPose = Pose.identity;

                //Android and iOS require coordinate from stored Anchor
#if UNITY_ANDROID || UNITY_IOS
            detectedAnchorPose = detectedAnchors[id].GetPose();
#endif

                var anchoredObject = GameObject.Instantiate(objectToAnchorPrefab, detectedAnchorPose.position, detectedAnchorPose.rotation);

                CloudNativeAnchor attachedAnchor = GetClouddNativeAnchor(anchoredObject);

                attachedAnchor.CloudToNative(detectedAnchors[id]);
                OnCloudAnchorUpdated($"{anchoredObject.name}-{id}", anchoredObject);
                return true;
            }

            return false;
        }

        /// <inheritdoc />
        public bool HasCloudAnchor(GameObject anchoredObject)
        {
            Debug.Assert(anchoredObject != null, "Anchored Object is null");

            var cloudAnchor = anchoredObject.GetComponent<CloudNativeAnchor>();
            return cloudAnchor != null && !string.IsNullOrEmpty(cloudAnchor.CloudAnchor.Identifier);
        }

        /// <inheritdoc />
        public bool MoveAnchoredObject(GameObject anchoredObject, Vector3 position, Quaternion rotation, string cloudAnchorID = "")
        {
            Debug.Assert(anchoredObject != null, "Anchored Object is null");

            var attachedAnchor = anchoredObject.GetComponent<CloudNativeAnchor>();
            if (attachedAnchor == null)
            {
                Debug.Assert(attachedAnchor != null, "No existing ASA Anchor to move");

                return false;
            }

            // if a Cloud identifier is provided and the corresponding ID has been found, move object to anchored point.
            // Else force move the anchor which breaks any preexisting cloud anchor reference.
            if (!string.IsNullOrEmpty(cloudAnchorID) && detectedAnchors.ContainsKey(cloudAnchorID))
            {
                attachedAnchor.CloudToNative(detectedAnchors[cloudAnchorID]);
            }
            else
            {
                attachedAnchor.SetPose(position, rotation);
            }

            OnCloudAnchorUpdated($"{anchoredObject.name}-{cloudAnchorID}", anchoredObject);
            return true;
        }

        /// <inheritdoc />
        public void DeleteAnchor(string id)
        {
            DeleteAnchors(new string[] { id });
        }

        /// <inheritdoc />
        public async void DeleteAnchors(string[] ids)
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
        public bool TryClearAnchors()
        {
            detectedAnchors = new Dictionary<string, CloudSpatialAnchor>();

            if (detectedAnchors?.Count == 0)
            {
                return true;
            }

            return false;
        }

        #endregion

        #region Events

        #region Internal Events

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
        public event Action CreateAnchoredObjectStarted;
        public void OnCreateAnchoredObjectStarted() => CreateAnchoredObjectStarted?.Invoke();

        /// <inheritdoc />
        public event Action AnchorLocated;
        public void OnAnchorLocated() => AnchorLocated?.Invoke();

        /// <inheritdoc />
        public event Action<string> AnchorDeleted;
        public void OnAnchorDeleted(string id) => AnchorDeleted?.Invoke(id);

        #endregion Internal Events

        #region Service Events

        /// <inheritdoc />
        public event Action CreateAnchoredObjectFailed;
        public void OnCreateAnchoredObjectFailed() => CreateAnchoredObjectFailed?.Invoke();

        /// <inheritdoc />
        public event Action<string, GameObject> CreateAnchoredObjectSucceeded;
        public void OnCreateAnchoredObjectSucceeded(string id, GameObject anchoredObject) => CreateAnchoredObjectSucceeded?.Invoke(id, anchoredObject);

        /// <inheritdoc />
        public event Action<string> SpatialPersistenceStatusMessage;
        public void OnSpatialPersistenceStatusMessage(string message) => SpatialPersistenceStatusMessage?.Invoke(message);

        /// <inheritdoc />
        public event Action<string> SpatialPersistenceError;
        public void OnSpatialPersistenceError(string exception) => SpatialPersistenceError?.Invoke(exception);

        /// <inheritdoc />
        public event Action<string, GameObject> CloudAnchorUpdated;
        public void OnCloudAnchorUpdated(string id, GameObject gameObject) => CloudAnchorUpdated?.Invoke(id, gameObject);

        /// <inheritdoc />
        public event Action<string> CloudAnchorLocated;
        public void OnCloudAnchorLocated(string id) => CloudAnchorLocated?.Invoke(id);

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
}
