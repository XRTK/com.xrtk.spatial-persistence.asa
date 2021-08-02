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

        private Dictionary<Guid, GameObject> anchors = new Dictionary<Guid, GameObject>();

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
                anchorService.CreateAnchorSucceeded += SpatialPersistenceSystem_CreateAnchorSucceeded;
                anchorService.CreateAnchorFailed += SpatialPersistenceSystem_CreateAnchorFailed;
                anchorService.SpatialPersistenceStatusMessage += SpatialPersistenceSystem_SpatialPersistenceStatusMessage;
                anchorService.AnchorLocated += SpatialPersistenceSystem_AnchorLocated;
                anchorService.AnchorUpdated += SpatialPersistenceSystem_AnchorUpdated;
                anchorService.SpatialPersistenceError += AnchorService_SpatialPersistenceError;
            }
        }

        private void AnchorService_SpatialPersistenceError(string message)
        {
            // Bad things happened, but what?
            UpdateStatusText(message, Color.red);
        }

        private void SpatialPersistenceSystem_AnchorUpdated(Guid anchorID, GameObject gameObject)
        {
            Debug.Log($"Anchor found [{anchorID}] and placed at [{gameObject.transform.position.ToString()}]-[{gameObject.transform.rotation.ToString()}]");
        }

        private void SpatialPersistenceSystem_AnchorLocated(Guid anchorID, GameObject gameObject)
        {
            //Attach a 3D Object to the Empty Anchor Object
            var locatedAnchor = GameObject.Instantiate(placedPrefab, gameObject.transform);
            locatedAnchor.GetComponent<MeshRenderer>().material.color = Color.blue;
        }

        private void SpatialPersistenceSystem_SpatialPersistenceStatusMessage(string statusMessage)
        {
            // If more data is required during the anchoring process, the Spatial Persistence system needs to feedback to the user.
            UpdateStatusText(statusMessage, Color.black);
        }

        private void SpatialPersistenceSystem_CreateAnchorFailed()
        {
            Debug.LogError("Anchor Failed to Create");
            UpdateStatusText($"Anchor Failed to Create", Color.red);
        }

        private void SpatialPersistenceSystem_CreateAnchorSucceeded(Guid anchorID, GameObject anchoredObject)
        {
            // Reset initial touched object
            GameObject.Destroy(spawnedObject);
            spawnedObject = null;

            // Cache Placed object for future use
            anchors.Add(anchorID, anchoredObject);

            // Place an Object on the new Anchor
            var placedAnchor = GameObject.Instantiate(placedPrefab, gameObject.transform);
            placedAnchor.GetComponent<MeshRenderer>().material.color = Color.magenta;

            // Update UI that placement was successful
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
            // Is there a touch, if not, return
            if (!TryGetTouchPosition(out Vector2 touchPosition))
            {
                return;
            }

            // Is the touch NOT on a configured UI area. If it hits UI no touch areas, return
            if (!ValidARTouchLocation(touchPosition))
            {
                return;
            }

            // Use the ARRaycast Manager to ray cast into the scene to hit a ARPlane
            if (m_RaycastManager.Raycast(touchPosition, s_Hits, TrackableType.PlaneWithinPolygon))
            {
                // Raycast hits are sorted by distance, so the first one
                // will be the closest hit.
                var hitPose = s_Hits[0].pose;

                if (spawnedObject == null)
                {
                    // If this is a new placement, place a temp object where it was touched
                    UpdateStatusText(string.Empty, Color.black);
                    spawnedObject = Instantiate(m_PlacedPrefab, hitPose.position, hitPose.rotation);
                    spawnedObject.GetComponent<MeshRenderer>().material.color = Color.red;

                    // Pass the touched position to the Spatial Persistence service to create an Anchor
                    anchorService?.TryCreateAnchor(hitPose.position, hitPose.rotation, DateTimeOffset.Now.AddDays(1));
                }
                else
                {
                    // Dumb code to move a touched area, above code should be separated to a "Create Anchor" method.
                    // Once the Anchor creation process has started, moving the object has no effect on the process.
                    spawnedObject.transform.position = hitPose.position;
                }
            }
        }

        static List<ARRaycastHit> s_Hits = new List<ARRaycastHit>();

        ARRaycastManager m_RaycastManager;

        public void ClearAndFindAnchors()
        {
            List<Guid> anchorIDs = new List<Guid>();
            foreach (KeyValuePair<Guid,GameObject> item in anchors)
            {
                GameObject.Destroy(item.Value);
                anchorIDs.Add(item.Key);
            }

            anchorService.TryClearAnchorCache();

            anchorService.TryFindAnchorPoints(anchorIDs.ToArray());
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