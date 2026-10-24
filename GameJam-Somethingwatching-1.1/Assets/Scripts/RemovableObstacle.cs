using UnityEngine;

/// <summary>
/// 可拆障碍标记：只有挂上本组件的障碍物才会被 ObstacleInteraction 判定为可交互目标。
/// 原始测试墙没有挂这个组件，因此保持不可拆毁。
///
/// 如果碰撞体在子物体上，把本组件挂在要整体删除的父物体上即可；
/// 读条完成后被删除的是挂载本组件的那个 GameObject 及其所有子物体。
/// </summary>
[DisallowMultipleComponent]
public class RemovableObstacle : MonoBehaviour
{
}
