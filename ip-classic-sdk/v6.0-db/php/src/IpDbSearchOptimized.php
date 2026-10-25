<?php

/**
 * @deprecated 1.1.0 IpDbSearchOptimized 已与 IpDbSearch 合并为同一实现
 *             (预解析 SplFixedArray + 字符串偏移量均为默认行为)。
 *             请改用 IpDbSearch::getInstance()。
 */

declare(strict_types=1);

namespace Qqzeng\Ip;

class IpDbSearchOptimized extends IpDbSearch
{
    /** @deprecated 请使用 IpDbSearch::getInstance()。 */
    public static function getInstance(): IpDbSearch
    {
        return IpDbSearch::getInstance();
    }
}
