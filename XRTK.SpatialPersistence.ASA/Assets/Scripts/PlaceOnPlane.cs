using System;
using System.Collections.Generic;
using UnityEngine.UI;
using UnityEngine.XR.ARSubsystems;
using XRTK.Interfaces.SpatialPersistence;
using XRTK.Services;

namespace UnityEngine.XR.ARFoundation.Samples
{
    /// <summary>
    /// Listens for touch events and performs an AR raycast from the screen touch point.
    /// AR raycasts will only hit detected trackables like feature points and planes.
    ///
    /// If a raycast hits a trackable, the <see cref="placedPrefab"/> is instantiated
    /// and moved to the hit position.
    /// </summary>
    [RequireComponent(typeof(ARRaycastManager))]
    public class PlaceOnPlane : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("Instantiates this prefab on a plane at the touch location.")]
        GameObject m_PlacedPrefab;

        public RectTransform[] ScreenUIToIgnore;

        [SerializeField]
        private Text textStatus;

        private IMixedRealitySpatialPersistenceSystem anchorService;

        private Dictionary<string, GameObject> anchors = new Dictionary<string, GameObject>();

        /// <summary>
        /// The prefab to instantiate on touch.
        /// </summary>
        public GameObject placedPrefab
        {
            get { return m_PlacedPrefab; }
            set { m_PlacedPrefab = value; }
        }

        /// <summary>
        /// The object instantiated as a result of a successful raycast intersection with a plane.
        /// </summary>
        public GameObject spawnedObject { get; private set; }

        void Start()
        {
            m_RaycastManager = GetComponent<ARRaycastManager>();
            if (MixedRealityToolkit.TryGetService<IMixedRealitySpatialPersistenceSystem>(out anchorService))
            {
                anchorService.CreateAnchoredObjectSucceeded += SpatialPersistenceSystem_CreateAnchoredObjectSucceeded;
                anchorService.CreateAnchoredObjectFailed += SpatialPersistenceSystem_CreateAnchoredObjectFailed;
                anchorService.SpatialPersistenceStatusMessage += SpatialPersistenceSystem_SpatialPersistenceStatusMessage;
                anchorService.CloudAnchorLocated += SpatialPersistenceSystem_CloudAnchorLocated;
                anchorService.CloudAnchorUpdated += SpatialPersistenceSystem_CloudAnchorUpdated;
                anchorService.SpatialPersistenceError += AnchorService_SpatialPersistenceError;
            }
        }

        private void AnchorService_SpatialPersistenceError(string message)
        {
            UpdateStatusText(message, Color.red);
        }

        private void SpatialPersistenceSystem_CloudAnchorUpdated(string anchorID, GameObject gameObject)
        {
            Debug.Log($"Anchor found [{anchorID}] and placed at [{gameObject.transform.position.ToString()}]-[{gameObject.transform.rotation.ToString()}]");
        }

        private void SpatialPersistenceSystem_CloudAnchorLocated(string anchorID)
        {
            if (anchors.ContainsKey(anchorID))
            {
                anchorService.PlaceSpatialPersistence(anchorID, m_PlacedPrefab);
            }
        }

        private void SpatialPersistenceSystem_SpatialPersistenceStatusMessage(string statusMessage)
        {
            UpdateStatusText(statusMessage, Color.black);
        }

        private void SpatialPersistenceSystem_CreateAnchoredObjectFailed()
        {
            Debug.LogError("Anchor Failed to Create");
            UpdateStatusText($"Anchor Failed to Create", Color.red);
        }

        private void SpatialPersistenceSystem_CreateAnchoredObjectSucceeded(string anchorID, GameObject anchoredObject)
        {
            anchors.Add(anchorID, anchoredObject);
            UpdateStatusText($"Anchor ID [{anchorID}] Saved", Color.green);
        }

        bool TryGetTouchPosition(out Vector2 touchPosition)
        {
            if (Input.touchCount > 0)
            {
                touchPosition = Input.GetTouch(0).position;
                return true;
            }

            touchPosition = default;
            return false;
        }

        void Update()
        {
            if (!TryGetTouchPosition(out Vector2 touchPosition))
                return;

            if (!ValidARTouchLocation(touchPosition))
                return;

            if (m_RaycastManager.Raycast(touchPosition, s_Hits, TrackableType.PlaneWithinPolygon))
            {
                // Raycast hits are sorted by distance, so the first one
                // will be the closest hit.
                var hitPose = s_Hits[0].pose;

                if (spawnedObject == null)
                {
                    UpdateStatusText(string.Empty, Color.black);
                    anchorService?.CreateAnchoredObject(m_PlacedPrefab, hitPose.position, hitPose.rotation, DateTimeOffset.Now.AddDays(1));
                    //spawnedObject = Instantiate(m_PlacedPrefab, hitPose.position, hitPose.rotation);
                }
                else
                {
                    spawnedObject.transform.position = hitPose.position;
                }
            }
        }

        static List<ARRaycastHit> s_Hits = new List<ARRaycastHit>();

        ARRaycastManager m_RaycastManager;

        public void ClearAndFindAnchors()
        {
            List<string> anchorIDs = new List<string>();
            foreach (KeyValuePair<string,GameObject> item in anchors)
            {
                GameObject.Destroy(item.Value);
                anchorIDs.Add(item.Key);
            }

            anchorService.TryClearAnchors();

            anchorService.FindAnchorPoints(anchorIDs.ToArray());
        }

        public bool ValidARTouchLocation(Vector2 touchPosition)
        {
            // Detects user tap and if not in UI, call OnSelectObjectInteraction above
            foreach (var rt in ScreenUIToIgnore)
            {
                if (RectTransformUtility.RectangleContainsScreenPoint(rt, touchPosition))
                {
                    return false;
                }
            }
            return true;
        }

        private void UpdateStatusText(string message, Color color)
        {
            textStatus.color = color;
            textStatus.text = message;
        }
    }
}