using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using Oculus.Interaction.Input;
using NUnit.Framework;

public class A1Interaction : MonoBehaviour
{
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    [SerializeField]
    private Hand leftHand; // TO DO: assign OVRLeftHandDataSource to this in the inspector
    [SerializeField]
    private Hand rightHand; // TO DO: assign OVRRightHandDataSource to this in the inspector

    [SerializeField]
    private TextMeshPro LeftHandPosText; // TO DO: assign a TextMeshPro object in the inspector

    [SerializeField]
    private TextMeshPro RightHandPosText; // TO DO: assign a TextMeshPro object in the inspector

    [SerializeField]
    private TextMeshPro StateText; // TO DO: assign a TextMeshPro object in the inspector

    private Pose leftHandPose;
    private Pose rightHandPose;
    private HandJointId handJointId = HandJointId.HandIndex3; // Note: you can change this to any bone you want, such as HandThumbTip, HandMiddleTip, etc.

    bool isThumbsUp = false;
    void Update()
    {
        if (leftHand != null && leftHand.GetJointPose(handJointId, out leftHandPose))
        {
            // Display the position and rotation of the left hand joint
            if (LeftHandPosText != null)
            {
                LeftHandPosText.text = $"Left Hand:\nPos: {leftHandPose.position.ToString("F2")}\nRot: {leftHandPose.rotation.eulerAngles.ToString("F2")}";
            }
        }

        if (rightHand != null && rightHand.GetJointPose(handJointId, out rightHandPose))
        {
            // Display the position and rotation of the right hand joint
            if (RightHandPosText != null)
            {
                RightHandPosText.text = $"Right Hand:\nPos: {rightHandPose.position.ToString("F2")}\nRot: {rightHandPose.rotation.eulerAngles.ToString("F2")}";
            }
        }

        if (isThumbsUp)
        {
            return;
        }

        // Check if two hand poses are close to each other
        if (leftHand != null && rightHand != null)
        {
            float distance = Vector3.Distance(leftHandPose.position, rightHandPose.position);

            if (distance < 0.1f)  // Less than 0.1 meters
            {
                StateText.text = $"Hands Too Close! Distance: {distance:F3}m";
            }
            else
            {
                StateText.text = $"No Thumbs Up";
                StateText.color = Color.yellow;
            }
        }
    }

    public void ThumbsUp()
    {
        StateText.text = "THUMBS UP DETECTED! ";
        isThumbsUp = true;
    }
    public void NoThumbsUp()
    {
        isThumbsUp = false;
    }
}